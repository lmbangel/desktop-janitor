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
    private readonly Dictionary<string, AppUsage> _apps = new();
    private readonly Dictionary<uint, string?> _processNames = new();

    public string? ForegroundApp { get; private set; }

    public void Tick(double seconds, bool userPresent)
    {
        // A minimised window can stay "foreground" if nothing else took focus; nobody is using it.
        IntPtr foreground = Native.GetForegroundWindow();
        ForegroundApp = Native.IsIconic(foreground) ? null : AppNameOf(foreground);
        if (ForegroundApp is not null && _apps.TryGetValue(ForegroundApp, out AppUsage? active))
        {
            active.IdleSeconds = 0;
            active.Trimmed = false;
            active.SeenInFront = true;
        }

        if (!userPresent) return;
        foreach ((string name, AppUsage usage) in _apps)
        {
            if (name != ForegroundApp) usage.IdleSeconds += seconds;
        }
    }

    /// <summary>Starts tracking newly seen apps at zero and forgets closed ones.</summary>
    public void Sync(IEnumerable<string> appNames)
    {
        var current = appNames.ToHashSet();
        foreach (string gone in _apps.Keys.Where(k => !current.Contains(k)).ToList())
            _apps.Remove(gone);
        foreach (string name in current)
            _apps.TryAdd(name, new AppUsage());
        _processNames.Clear(); // PIDs get reused; don't trust old entries for long
    }

    public AppUsage? Get(string appName) => _apps.TryGetValue(appName, out AppUsage? usage) ? usage : null;

    private string? AppNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (_processNames.TryGetValue(pid, out string? cached)) return cached;

        string? name = null;
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            name = AppNames.Normalize(p.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
        _processNames[pid] = name;
        return name;
    }
}
