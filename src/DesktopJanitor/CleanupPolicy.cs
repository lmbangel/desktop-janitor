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
        if (app.MemoryBytes < config.MinMemoryMB * Bytes.MB) return $"skipped: under {config.MinMemoryMB} MB";
        if (config.OnlyMinimized && !app.AllMinimized) return "waiting: has a window that isn't minimised";
        if (app.Name == foregroundApp) return "in use right now";
        if (usage is null || usage.IdleSeconds < config.IdleMinutes * 60) return $"fine: suggested only after {config.IdleMinutes:0} min unused";
        if (usage.SnoozedUntil > DateTime.UtcNow) return "snoozed";
        return null;
    }

    /// <summary>For your own clicks in the memory list: hard protections and the deny list still apply.</summary>
    public static bool MayActOnManually(AppInfo app, JanitorConfig config) =>
        app.ProtectedReason is null && !AppNames.Contains(config.DenyList, app.Name);

    /// <summary>For trimming only: protections and your lists apply, idle time and size don't.</summary>
    public static bool MayTrim(AppInfo app, string? foregroundApp, JanitorConfig config) =>
        app.ProtectedReason is null
        && !AppNames.Contains(config.DenyList, app.Name)
        && (config.AllowList.Count == 0 || AppNames.Contains(config.AllowList, app.Name))
        && app.Name != foregroundApp;
}
