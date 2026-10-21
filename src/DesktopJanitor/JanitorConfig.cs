using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopJanitor;

public enum CleanupMode
{
    /// <summary>Suggest idle apps in a speech bubble; nothing closes without your click.</summary>
    Ask,
    /// <summary>Never close anything; quietly trim idle apps' working sets instead.</summary>
    TrimOnly,
    /// <summary>Just the sweeper.</summary>
    Off,
}

public sealed class AnimationConfig
{
    public int Row { get; set; }
    public int Frames { get; set; }
    public double Fps { get; set; }
}

public sealed class SpriteConfig
{
    /// <summary>Path to a custom sprite sheet PNG; empty uses the built-in janitor.</summary>
    public string? Path { get; set; }
    public int FrameWidth { get; set; } = 44;
    public int FrameHeight { get; set; } = 32;
    public AnimationConfig Walk { get; set; } = new() { Row = 0, Frames = 8, Fps = 10 };
    public AnimationConfig Sweep { get; set; } = new() { Row = 1, Frames = 6, Fps = 10 };
    public AnimationConfig Idle { get; set; } = new() { Row = 2, Frames = 4, Fps = 3 };
}

public sealed class JanitorConfig
{
    public CleanupMode Mode { get; set; } = CleanupMode.Ask;

    /// <summary>Minutes of your active time an app must go without being in front before it counts as unused.</summary>
    public double IdleMinutes { get; set; } = 30;

    /// <summary>Only suggest apps whose windows are all minimised.</summary>
    public bool OnlyMinimized { get; set; }

    /// <summary>Ignore apps using less than this (all their processes together).</summary>
    public int MinMemoryMB { get; set; } = 150;

    /// <summary>No input for this long means you're away: idle clocks pause and nothing is suggested.</summary>
    public double PresenceTimeoutMinutes { get; set; } = 2;

    public double SnoozeMinutes { get; set; } = 60;
    public double PromptCooldownMinutes { get; set; } = 5;
    public int PromptTimeoutSeconds { get; set; } = 60;

    /// <summary>If not empty, only these apps are ever suggested.</summary>
    public List<string> AllowList { get; set; } = new();

    /// <summary>Idle apps closed (gracefully) without asking. Empty = always ask.</summary>
    public List<string> AutoCloseApps { get; set; } = new();

    /// <summary>Apps the janitor never suggests, closes or trims. "Never" in the bubble adds to this.</summary>
    public List<string> DenyList { get; set; } = new()
    {
        // Editors and IDEs
        "claude", "code", "cursor", "devenv", "phpstorm64", "rider64", "idea64", "sublime_text", "notepad++",
        // Terminals
        "windowsterminal", "openconsole", "cmd", "powershell", "pwsh", "mintty", "warp", "wezterm-gui",
        "alacritty", "tabby", "hyper",
        // Databases, APIs, local servers, file transfer
        "heidisql", "sqlyog", "dbeaver", "mysqlworkbench", "ssms", "datagrip64", "tableplus", "postman",
        "docker desktop", "laragon", "xampp-control", "wampmanager", "filezilla", "winscp",
        // Passwords, recording, remote access, calls
        "1password", "keepassxc", "bitwarden", "obs64", "vmware", "virtualbox", "anydesk", "teamviewer",
        "zoom", "ms-teams", "teams",
    };

    /// <summary>Click him for the memory list. False makes him fully click-through.</summary>
    public bool Clickable { get; set; } = true;

    /// <summary>How many apps the memory list shows (biggest first).</summary>
    public int MemoryListSize { get; set; } = 6;

    public double WalkSpeed { get; set; } = 40;
    public double Scale { get; set; } = 3;
    public bool SweepBreaks { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public SpriteConfig Sprite { get; set; } = new();
}

public static class AppNames
{
    /// <summary>"Spotify.exe" / " SPOTIFY " -> "spotify", matching process names.</summary>
    public static string Normalize(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return n.EndsWith(".exe", StringComparison.Ordinal) ? n[..^4] : n;
    }

    public static bool Contains(IEnumerable<string> list, string appName) =>
        list.Any(n => Normalize(n) == appName);
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopJanitor", "config.json");

    public static string FilePath { get; private set; } = DefaultPath;

    public static JanitorConfig Current { get; private set; } = new();

    public static JanitorConfig Load(string? path = null)
    {
        FilePath = path ?? FilePath;
        if (!File.Exists(FilePath))
        {
            Current = new JanitorConfig();
            Save();
            Log.Info($"Wrote default config to {FilePath}");
            return Current;
        }

        try
        {
            Current = JsonSerializer.Deserialize<JanitorConfig>(File.ReadAllText(FilePath), Options) ?? new JanitorConfig();
        }
        catch (Exception ex)
        {
            // Keep the user's file as-is so they can fix it; run on defaults meanwhile.
            Log.Info($"Config at {FilePath} is invalid ({ex.Message}); using defaults.");
            Current = new JanitorConfig();
        }
        return Current;
    }

    public static void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
    }
}
