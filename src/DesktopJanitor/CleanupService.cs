using System.Diagnostics;
using System.Windows.Threading;

namespace DesktopJanitor;

public enum CloseOutcome { Closed, StillOpen, InBackground, NotSent, NotAllowed }

/// <summary>What a graceful close achieved; LeftoverBytes is what's still running under that name.</summary>
public sealed record CloseResult(CloseOutcome Outcome, long LeftoverBytes);

/// <summary>
/// Watches what you use, and when an app has sat unused long enough, has the janitor ask about it.
/// Closing is always graceful (WM_CLOSE, like clicking X) and is never forced.
/// </summary>
public sealed class CleanupService
{
    public static readonly IReadOnlyList<BubbleButton> QuestionButtons = new[]
    {
        new BubbleButton("Close it", PromptChoice.Close, Primary: true),
        new BubbleButton("Trim RAM", PromptChoice.Trim),
        new BubbleButton("Not now", PromptChoice.NotNow),
        new BubbleButton("Never", PromptChoice.Never),
    };

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(8);

    private readonly JanitorWindow _janitor;
    private readonly UsageTracker _tracker = new();
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _scanTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private List<AppInfo> _apps = new();
    private double _lastTick;
    private DateTime _lastPrompt = DateTime.MinValue;
    private bool _scanning;
    private bool _busy;

    private sealed record Candidate(AppInfo App, AppUsage Usage);

    public CleanupService(JanitorWindow janitor)
    {
        _janitor = janitor;
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => Tick();
        _scanTimer = new DispatcherTimer { Interval = ScanInterval };
        _scanTimer.Tick += async (_, _) => await ScanAndEvaluateAsync(manual: false);
    }

    private static JanitorConfig Config => ConfigStore.Current;

    private static bool UserPresent => Native.UserIdleTime().TotalMinutes < Config.PresenceTimeoutMinutes;

    public void Start()
    {
        _tickTimer.Start();
        _scanTimer.Start();
        _ = ScanAndEvaluateAsync(manual: false);
    }

    public void Stop()
    {
        _tickTimer.Stop();
        _scanTimer.Stop();
    }

    /// <summary>Tray: look now, ignoring the cool-down between questions.</summary>
    public Task CheckNowAsync()
    {
        if (_busy)
        {
            _janitor.Say("One thing at a time!");
            return Task.CompletedTask;
        }
        return ScanAndEvaluateAsync(manual: true);
    }

    /// <summary>Tray: trim every background app that isn't protected or on your deny list.</summary>
    public async Task SweepMemoryNowAsync()
    {
        await ScanAsync();
        var targets = _apps.Where(a => CleanupPolicy.MayTrim(a, _tracker.ForegroundApp, Config)).ToList();
        long freed = await TrimAsync(targets);
        _janitor.Say($"Swept {Bytes.Format(freed)} out of RAM", "Those apps keep running and pull back what they need.");
    }

    public async Task ShowReportAsync()
    {
        await ScanAsync();
        string text = Report.Build(_apps, a => _tracker.Get(a.Name), _tracker.ForegroundApp, Config);
        Report.Write(text, open: true);
    }

    // --- Memory list (click the janitor) ---

    public async Task<IReadOnlyList<AppInfo>> RefreshAppsAsync()
    {
        await ScanAsync();
        return _apps;
    }

    public AppUsage? UsageOf(AppInfo app) => _tracker.Get(app.Name);

    /// <summary>Your click on Close: the same graceful close; protections and the deny list still apply.</summary>
    public async Task<CloseResult> CloseFromListAsync(AppInfo app)
    {
        if (!CleanupPolicy.MayActOnManually(app, Config)) return new CloseResult(CloseOutcome.NotAllowed, 0);
        // Don't follow your own click with a question about the same app.
        if (_tracker.Get(app.Name) is { } usage) usage.SnoozedUntil = DateTime.UtcNow.AddMinutes(Config.SnoozeMinutes);
        return await CloseGracefullyAsync(app, "Closing (from the memory list)");
    }

    public async Task<long> TrimFromListAsync(AppInfo app)
    {
        if (!CleanupPolicy.MayActOnManually(app, Config)) return 0;
        return await TrimAsync(new[] { app });
    }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        // Cap the step so a sleep/hibernate gap isn't counted as idle time.
        double seconds = Math.Min(now - _lastTick, 5);
        _lastTick = now;
        _tracker.Tick(seconds, UserPresent);
    }

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            _apps = await Task.Run(AppScanner.Scan);
            _tracker.Sync(_apps.Select(a => a.Name));
        }
        catch (Exception ex)
        {
            Log.Info($"Scan failed: {ex.Message}");
        }
        finally
        {
            _scanning = false;
        }
    }

    private List<Candidate> Candidates(JanitorConfig config)
    {
        var result = new List<Candidate>();
        foreach (AppInfo app in _apps) // already sorted biggest first
        {
            AppUsage? usage = _tracker.Get(app.Name);
            if (usage is not null && CleanupPolicy.WhyNot(app, usage, _tracker.ForegroundApp, config) is null)
                result.Add(new Candidate(app, usage));
        }
        return result;
    }

    private async Task ScanAndEvaluateAsync(bool manual)
    {
        await ScanAsync();
        if (_busy) return;

        JanitorConfig config = Config;
        if (config.Mode == CleanupMode.Off)
        {
            if (manual) _janitor.Say("Cleanup is switched off", "I'm just sweeping. Turn it on from the tray menu.");
            return;
        }
        if (!manual && !UserPresent) return;

        List<Candidate> candidates = Candidates(config);

        if (config.Mode == CleanupMode.TrimOnly)
        {
            var untrimmed = candidates.Where(c => !c.Usage.Trimmed).ToList();
            if (untrimmed.Count == 0)
            {
                if (manual) SayAllTidy(config);
                return;
            }
            long freed = await TrimAsync(untrimmed.Select(c => c.App).ToList());
            _janitor.Say($"Swept {Bytes.Format(freed)} out of idle apps",
                string.Join(", ", untrimmed.Select(c => c.App.DisplayName)));
            return;
        }

        _busy = true;
        try
        {
            // Apps you listed for auto-close go without a question (still gracefully).
            foreach (Candidate c in candidates.Where(c => AppNames.Contains(config.AutoCloseApps, c.App.Name)).ToList())
                await CloseAsync(c, asked: false);

            Candidate? next = candidates.FirstOrDefault(c => !AppNames.Contains(config.AutoCloseApps, c.App.Name));
            if (next is null)
            {
                if (manual) SayAllTidy(config);
                return;
            }
            if (!manual && DateTime.UtcNow - _lastPrompt < TimeSpan.FromMinutes(config.PromptCooldownMinutes)) return;
            if (!_janitor.CanTalk) return;

            await AskAsync(next, config);
        }
        finally
        {
            _busy = false;
        }
    }

    private void SayAllTidy(JanitorConfig config) =>
        _janitor.Say("All tidy!", $"Nothing has gone unused for {config.IdleMinutes:0} min.");

    private async Task AskAsync(Candidate c, JanitorConfig config)
    {
        string name = c.App.DisplayName;
        string memory = Bytes.Format(c.App.MemoryBytes);
        Log.Info($"Asking about {c.App.Name} ({memory}, unused {c.Usage.IdleMinutes:0} min)");

        PromptChoice choice = await _janitor.AskAsync(
            $"{name} hasn't been used for {c.Usage.IdleMinutes:0} min",
            $"It's holding {memory}. Want me to close it? It'll still get to ask you to save anything.",
            QuestionButtons,
            TimeSpan.FromSeconds(config.PromptTimeoutSeconds));

        _lastPrompt = DateTime.UtcNow;
        Log.Info($"Answer for {c.App.Name}: {choice}");

        // You might have switched to it while the bubble was up.
        if ((choice is PromptChoice.Close or PromptChoice.Trim) && _tracker.Get(c.App.Name)?.IdleSeconds < config.IdleMinutes * 60)
        {
            _janitor.Say($"You're using {name} again", "So I'll leave it be.");
            return;
        }

        switch (choice)
        {
            case PromptChoice.Close:
                await CloseAsync(c, asked: true);
                break;
            case PromptChoice.Trim:
                long freed = await TrimAsync(new[] { c.App });
                Snooze(c);
                _janitor.Say($"Swept {Bytes.Format(freed)} out of {name}", "It keeps running and pulls back what it needs.");
                break;
            case PromptChoice.Never:
                // Current, not the captured config: it may have been reloaded while the bubble was up.
                ConfigStore.Current.DenyList.Add(c.App.Name);
                ConfigStore.Save();
                Log.Info($"Added {c.App.Name} to the deny list");
                _janitor.Say($"Got it, I'll never touch {name}", "Added it to the deny list in your config.");
                break;
            case PromptChoice.NotNow:
                Snooze(c);
                _janitor.Say("Okay, I'll ask again later");
                break;
            default:
                Snooze(c);
                break;
        }
    }

    private static void Snooze(Candidate c) =>
        c.Usage.SnoozedUntil = DateTime.UtcNow.AddMinutes(Config.SnoozeMinutes);

    /// <summary>Posts WM_CLOSE to the app's windows (same as clicking X), then checks what happened.</summary>
    private async Task CloseAsync(Candidate c, bool asked)
    {
        AppInfo app = c.App;
        Snooze(c);
        CloseResult result = await CloseGracefullyAsync(app, asked ? "Closing" : "Auto-closing");

        switch (result.Outcome)
        {
            case CloseOutcome.NotSent:
                _janitor.Say($"{app.DisplayName} wouldn't take my note", "Nothing was closed.");
                break;
            case CloseOutcome.StillOpen:
                _janitor.Say($"{app.DisplayName} is still open", "It may be asking you to save, or it said no. I won't force it.");
                break;
            case CloseOutcome.InBackground:
                _janitor.Say($"Closed {app.DisplayName}'s window", $"It's still running in the background ({Bytes.Format(result.LeftoverBytes)}). Quit it from its tray icon to free the rest.");
                break;
            default:
                _janitor.Say($"Closed {app.DisplayName}", $"That's about {Bytes.Format(app.MemoryBytes)} swept up.");
                break;
        }
    }

    /// <summary>Posts WM_CLOSE to the app's windows (same as clicking X), waits, then checks what happened.</summary>
    private async Task<CloseResult> CloseGracefullyAsync(AppInfo app, string how)
    {
        int sent = 0;
        foreach (AppWindow w in app.Windows)
        {
            if (!IsStillSameWindow(w)) continue;
            if (Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) sent++;
        }
        Log.Info($"{how} {app.Name}: WM_CLOSE sent to {sent} of {app.Windows.Count} window(s)");
        if (sent == 0) return new CloseResult(CloseOutcome.NotSent, 0);

        await Task.Delay(CloseGracePeriod);

        bool windowsLeft = app.Windows.Any(w => IsStillSameWindow(w) && Native.IsWindowVisible(w.Handle));
        long leftover = await Task.Run(() => RunningMemory(app.Name));
        Log.Info($"After closing {app.Name}: windows left={windowsLeft}, still running={Bytes.Format(leftover)}");

        if (windowsLeft) return new CloseResult(CloseOutcome.StillOpen, leftover);
        return leftover > 0 ? new CloseResult(CloseOutcome.InBackground, leftover) : new CloseResult(CloseOutcome.Closed, 0);
    }

    private static bool IsStillSameWindow(AppWindow w)
    {
        if (!Native.IsWindow(w.Handle)) return false;
        Native.GetWindowThreadProcessId(w.Handle, out uint pid);
        return pid == w.ProcessId; // handles get reused
    }

    private static long RunningMemory(string appName)
    {
        int session = Process.GetCurrentProcess().SessionId;
        long total = 0;
        foreach (Process p in Process.GetProcessesByName(appName))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId == session) total += p.WorkingSet64;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return total;
    }

    /// <summary>EmptyWorkingSet on each of the apps' processes. Harmless: nothing closes, pages come back on demand.</summary>
    private async Task<long> TrimAsync(IReadOnlyList<AppInfo> apps)
    {
        long total = await Task.Run(() =>
        {
            int session = Process.GetCurrentProcess().SessionId;
            long freed = 0;
            foreach (AppInfo app in apps)
            {
                long appFreed = 0;
                foreach (Process p in Process.GetProcessesByName(app.Name))
                {
                    using (p)
                    {
                        try
                        {
                            if (p.SessionId != session) continue;
                        }
                        catch (InvalidOperationException)
                        {
                            continue;
                        }

                        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, (uint)p.Id);
                        if (h == IntPtr.Zero) continue;
                        try
                        {
                            long before = Native.GetWorkingSet(h);
                            if (Native.EmptyWorkingSet(h)) appFreed += Math.Max(0, before - Native.GetWorkingSet(h));
                        }
                        finally
                        {
                            Native.CloseHandle(h);
                        }
                    }
                }
                Log.Info($"Trimmed {app.Name}: {Bytes.Format(appFreed)}");
                freed += appFreed;
            }
            return freed;
        });

        foreach (AppInfo app in apps)
        {
            if (_tracker.Get(app.Name) is { } usage) usage.Trimmed = true;
        }
        return total;
    }
}
