using System.Diagnostics;

namespace DesktopJanitor;

public sealed class AppUsage
{
    /// <summary>Seconds of your active time since this app was last in front.</summary>
    public double IdleSeconds { get; set; }
    public DateTime SnoozedUntil { get; set; }
    public bool Trimmed { get; set; }

    /// <summary>False until the app has been in front while the janitor was watching.</summary>
    public bool SeenInFront { get; set; }

    public double IdleMinutes => IdleSeconds / 60;
}

/// <summary>
/// "Unused" = not the foreground app for N minutes while you were actually at the PC.
/// Time away (no input, locked, asleep) doesn't count, so coming back never triggers a flood of questions.
/// Apps start at zero when first seen, so nothing is suggested in the first N minutes after start-up.
/// </summary>
public sealed class UsageTracker
{
    // Keyed by AppInfo.Key (the program's exe path), so two programs that share a process name stay apart.
    private readonly Dictionary<string, AppUsage> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string?> _processKeys = new();

    /// <summary>Key of the app in front right now (see AppInfo.Key), if any.</summary>
    public string? ForegroundApp { get; private set; }

    public void Tick(double seconds, bool userPresent)
    {
        // A minimised window can stay "foreground" if nothing else took focus; nobody is using it.
        IntPtr foreground = Native.GetForegroundWindow();
        ForegroundApp = Native.IsIconic(foreground) ? null : KeyOf(foreground);
        if (ForegroundApp is not null && _apps.TryGetValue(ForegroundApp, out AppUsage? active))
        {
            active.IdleSeconds = 0;
            active.Trimmed = false;
            active.SeenInFront = true;
        }

        if (!userPresent) return;
        foreach ((string key, AppUsage usage) in _apps)
        {
            if (!string.Equals(key, ForegroundApp, StringComparison.OrdinalIgnoreCase)) usage.IdleSeconds += seconds;
        }
    }

    /// <summary>Starts tracking newly seen apps at zero and forgets closed ones.</summary>
    public void Sync(IEnumerable<string> appKeys)
    {
        var current = appKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string gone in _apps.Keys.Where(k => !current.Contains(k)).ToList())
            _apps.Remove(gone);
        foreach (string key in current)
            _apps.TryAdd(key, new AppUsage());
        _processKeys.Clear(); // PIDs get reused; don't trust old entries for long
    }

    public AppUsage? Get(string appKey) => _apps.TryGetValue(appKey, out AppUsage? usage) ? usage : null;

    private string? KeyOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (!_processKeys.TryGetValue(pid, out string? key))
            _processKeys[pid] = key = AppScanner.KeyFor(pid);
        return key;
    }
}
