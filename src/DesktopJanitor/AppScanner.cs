using System.Diagnostics;
using System.IO;

namespace DesktopJanitor;

public sealed record AppWindow(IntPtr Handle, uint ProcessId, string Title, bool Minimized);

/// <summary>One app as you'd see it in Alt+Tab: its windows plus all its same-named processes.</summary>
public sealed class AppInfo
{
    public AppInfo(string name, string displayName)
    {
        Name = name;
        DisplayName = displayName;
    }

    /// <summary>Normalised process name, e.g. "spotify". This is what config lists match.</summary>
    public string Name { get; }
    public string DisplayName { get; }
    public List<AppWindow> Windows { get; } = new();
    public long MemoryBytes { get; set; }
    public int ProcessCount { get; set; }
    public string? ExePath { get; set; }

    /// <summary>Why the janitor must never touch this app; null means it may be suggested.</summary>
    public string? ProtectedReason { get; set; }

    public bool AllMinimized => Windows.All(w => w.Minimized);
}

/// <summary>Finds apps with real, visible windows and marks the ones that are off limits.</summary>
public static class AppScanner
{
    // Hard safety list: never configurable. Most also live under C:\Windows, which is protected too.
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "explorer", "sihost", "taskhostw", "ctfmon", "runtimebroker",
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "searchui",
        "textinputhost", "lockapp", "applicationframehost", "systemsettings", "securityhealthsystray",
        "securityhealthservice", "sechealthui", "msmpeng", "nissrv", "smartscreen", "taskmgr", "conhost",
        "dllhost", "audiodg", "spoolsv", "widgets", "widgetservice", "phoneexperiencehost", "gamebar",
        "shellhost", "credentialuibroker", "consent", "logonui", "userinit", "mmc", "regedit",
        "msiexec", "setup", "installer", "unins000", "update", "updater",
    };

    private static readonly HashSet<string> SecurityProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "avgui", "avastui", "bdagent", "seccenter", "egui", "avp", "avpui", "mbam", "mbamtray",
        "mcuicnt", "nortonui", "msascuil",
    };

    // Anything installed under these folders counts as security software and is never touched.
    private static readonly string[] SecurityVendorFolders =
    {
        @"\avg\", @"\avast software\", @"\bitdefender\", @"\eset\", @"\kaspersky lab\", @"\malwarebytes\",
        @"\mcafee\", @"\norton\", @"\norton security\", @"\sophos\", @"\trend micro\", @"\windows defender\",
    };

    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public static List<AppInfo> Scan()
    {
        using Process me = Process.GetCurrentProcess();
        int session = me.SessionId;
        uint myPid = (uint)me.Id;

        var windows = new List<AppWindow>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (IsAppWindow(hwnd))
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                windows.Add(new AppWindow(hwnd, pid, Native.GetWindowTitle(hwnd), Native.IsIconic(hwnd)));
            }
            return true;
        }, IntPtr.Zero);

        var apps = new Dictionary<string, AppInfo>();
        foreach (IGrouping<uint, AppWindow> group in windows.GroupBy(w => w.ProcessId))
        {
            ProcessFacts? facts = Inspect(group.Key, session, myPid);
            if (facts is null) continue; // exited meanwhile

            if (!apps.TryGetValue(facts.Name, out AppInfo? app))
            {
                app = new AppInfo(facts.Name, facts.DisplayName);
                apps.Add(facts.Name, app);
            }
            app.Windows.AddRange(group);
            app.ExePath ??= facts.Path;
            // If any process behind the app is off limits, the whole app is.
            app.ProtectedReason ??= facts.ProtectedReason;
        }

        // Memory counts every same-named process in this session (browsers, Electron apps, etc. are many).
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session) continue;
                    if (apps.TryGetValue(AppNames.Normalize(p.ProcessName), out AppInfo? app))
                    {
                        app.MemoryBytes += p.WorkingSet64;
                        app.ProcessCount++;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Exited while we looked.
                }
            }
        }

        return apps.Values.OrderByDescending(a => a.MemoryBytes).ToList();
    }

    /// <summary>Roughly the Alt+Tab rule: visible, unowned, titled, not a tool window, not cloaked.</summary>
    private static bool IsAppWindow(IntPtr hwnd) =>
        Native.IsWindowVisible(hwnd)
        && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero
        && !Native.HasExtendedStyle(hwnd, Native.WS_EX_TOOLWINDOW)
        && Native.GetWindowTextLength(hwnd) > 0
        && !Native.IsCloaked(hwnd);

    private sealed record ProcessFacts(string Name, string DisplayName, string? Path, string? ProtectedReason);

    private static ProcessFacts? Inspect(uint pid, int session, uint myPid)
    {
        string name, display;
        int processSession;
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            name = AppNames.Normalize(p.ProcessName);
            display = p.ProcessName;
            processSession = p.SessionId;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }

        string? reason = null;
        if (pid == myPid) reason = "that's me";
        else if (processSession != session) reason = "belongs to another Windows session";
        else if (SystemProcesses.Contains(name)) reason = "Windows system process";
        else if (SecurityProcesses.Contains(name)) reason = "security software";

        IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return new ProcessFacts(name, display, null, reason ?? "can't be inspected (access denied)");

        string? path;
        try
        {
            path = Native.GetProcessPath(handle);
            if (path is null)
            {
                reason ??= "can't be inspected (no path)";
            }
            else
            {
                display = Describe(path) ?? display;
                if (path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) reason ??= "part of Windows";
                if (SecurityVendorFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))) reason ??= "security software";
            }

            bool? elevated = Native.IsElevated(handle);
            if (elevated is null) reason ??= "can't be inspected (token access denied)";
            else if (elevated.Value) reason ??= "runs as administrator";
        }
        finally
        {
            Native.CloseHandle(handle);
        }
        return new ProcessFacts(name, display, path, reason);
    }

    private static string? Describe(string path)
    {
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
