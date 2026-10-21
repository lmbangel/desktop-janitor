using System.IO;
using System.Text;

namespace DesktopJanitor;

/// <summary>Plain-text "what's open and what would the janitor do with it" report.</summary>
public static class Report
{
    public static string FilePath { get; } = Path.Combine(Log.Folder, "report.txt");

    public static string Build(IReadOnlyList<AppInfo> apps, Func<AppInfo, AppUsage?> usageOf, string? foregroundApp, JanitorConfig config)
    {
        var text = new StringBuilder();
        text.AppendLine($"Desktop Janitor - open apps at {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine($"Mode: {config.Mode}. Suggests apps unused for {config.IdleMinutes:0} min of your active time and using at least {config.MinMemoryMB} MB.");
        text.AppendLine($"Config: {ConfigStore.FilePath}");
        text.AppendLine();
        text.AppendLine($"{"APP",-40} {"MEMORY",9} {"UNUSED",8} {"WINDOWS",8}  STATUS");

        foreach (AppInfo app in apps)
        {
            AppUsage? usage = usageOf(app);
            string label = $"{app.DisplayName} ({app.Name})";
            if (label.Length > 40) label = label[..37] + "...";
            string unused = usage is null ? "-" : $"{usage.IdleMinutes:0} min";
            string status = CleanupPolicy.WhyNot(app, usage, foregroundApp, config) ?? "candidate: will be suggested";
            text.AppendLine($"{label,-40} {Bytes.Format(app.MemoryBytes),9} {unused,8} {app.Windows.Count,8}  {status}");
        }

        text.AppendLine();
        text.AppendLine("Only apps with a visible window are listed. Memory adds up every process with the same name.");
        text.AppendLine("Protected apps are never closed or trimmed, whatever the config says.");
        return text.ToString();
    }

    public static void Write(string text, bool open)
    {
        Directory.CreateDirectory(Log.Folder);
        File.WriteAllText(FilePath, text);
        if (open) Shell.Open(FilePath);
    }
}
