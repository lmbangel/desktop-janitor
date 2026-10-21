namespace DesktopJanitor;

public static class Bytes
{
    public const long MB = 1024 * 1024;

    public static string Format(long bytes) =>
        bytes >= 1024 * MB ? $"{bytes / (1024.0 * MB):0.0} GB" : $"{bytes / (double)MB:0} MB";
}

/// <summary>Decides whether an app may be suggested. Hard protections always win over config.</summary>
public static class CleanupPolicy
{
    /// <summary>Why this app is not a cleanup candidate right now, or null if it is one.</summary>
    public static string? WhyNot(AppInfo app, AppUsage? usage, string? foregroundApp, JanitorConfig config)
    {
        if (app.ProtectedReason is { } reason) return $"protected: {reason}";
        if (AppNames.Contains(config.DenyList, app.Name)) return "skipped: on your deny list";
        if (config.AllowList.Count > 0 && !AppNames.Contains(config.AllowList, app.Name)) return "skipped: not on your allow list";
        if (!app.HasWindows) return "background process: no window to close";
        if (app.MemoryBytes < config.MinMemoryMB * Bytes.MB) return $"skipped: under {config.MinMemoryMB} MB";
        if (config.OnlyMinimized && !app.AllMinimized) return "waiting: has a window that isn't minimised";
        if (IsForeground(app, foregroundApp)) return "in use right now";
        if (usage is null || usage.IdleSeconds < config.IdleMinutes * 60) return $"fine: suggested only after {config.IdleMinutes:0} min unused";
        if (usage.SnoozedUntil > DateTime.UtcNow) return "snoozed";
        return null;
    }

    /// <summary>
    /// Your own clicks in the memory list: only the hard protections apply. The deny list keeps the
    /// janitor from suggesting or auto-closing an app; it doesn't stop you (the list asks you to confirm).
    /// </summary>
    public static bool MayActOnManually(AppInfo app) => app.ProtectedReason is null;

    public static bool IsDenied(AppInfo app, JanitorConfig config) => AppNames.Contains(config.DenyList, app.Name);

    /// <summary>For trimming only: protections and your lists apply, idle time and size don't.</summary>
    public static bool MayTrim(AppInfo app, string? foregroundApp, JanitorConfig config) =>
        app.ProtectedReason is null
        && !AppNames.Contains(config.DenyList, app.Name)
        && (config.AllowList.Count == 0 || AppNames.Contains(config.AllowList, app.Name))
        && !IsForeground(app, foregroundApp);

    private static bool IsForeground(AppInfo app, string? foregroundApp) =>
        string.Equals(app.Key, foregroundApp, StringComparison.OrdinalIgnoreCase);
}
