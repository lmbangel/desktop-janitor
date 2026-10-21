using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace DesktopJanitor;

public sealed record AppWindow(IntPtr Handle, uint ProcessId, string Title, bool Minimized);

/// <summary>
/// One program as Task Manager groups it: every process started from the same exe, with
/// their combined private working set, plus any visible windows they own.
/// </summary>
public sealed class AppInfo
{
    public AppInfo(string key, string name, string displayName)
    {
        Key = key;
        Name = name;
        DisplayName = displayName;
    }

    /// <summary>Groups processes: the exe's full path (or "name:x" when it can't be read).</summary>
    public string Key { get; }

    /// <summary>Normalised process name, e.g. "spotify". This is what config lists match.</summary>
    public string Name { get; }
    public string DisplayName { get; }
    public List<AppWindow> Windows { get; } = new();
    public List<uint> ProcessIds { get; } = new();

    /// <summary>Private working set of all its processes: the figure in Task Manager's Memory column.</summary>
    public long MemoryBytes { get; set; }
    public int ProcessCount { get; set; }
    public string? ExePath { get; set; }

    /// <summary>Why the janitor must never touch this app; null means it may be suggested.</summary>
    public string? ProtectedReason { get; set; }

    public bool HasWindows => Windows.Count > 0;
    public bool AllMinimized => Windows.All(w => w.Minimized);
}

/// <summary>Groups every process in your session by program, and marks the ones that are off limits.</summary>
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

    // File descriptions don't change while an exe exists, and reading them for every process each scan adds up.
    private static readonly ConcurrentDictionary<string, string?> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    public static List<AppInfo> Scan()
    {
        using Process me = Process.GetCurrentProcess();
        int session = me.SessionId;
        uint myPid = (uint)me.Id;

        var windowsByPid = new Dictionary<uint, List<AppWindow>>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (IsAppWindow(hwnd))
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!windowsByPid.TryGetValue(pid, out List<AppWindow>? list))
                    windowsByPid[pid] = list = new List<AppWindow>();
                list.Add(new AppWindow(hwnd, pid, Native.GetWindowTitle(hwnd), Native.IsIconic(hwnd)));
            }
            return true;
        }, IntPtr.Zero);

        var apps = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        var access = new Dictionary<AppInfo, AccessSummary>();
        foreach (Native.ProcessSample sample in Snapshot())
        {
            if (sample.SessionId != session || sample.Pid == 0) continue;
            ProcessFacts facts = Inspect(sample, myPid);
            string key = facts.Path ?? "name:" + facts.Name;

            if (!apps.TryGetValue(key, out AppInfo? app))
            {
                app = new AppInfo(key, facts.Name, facts.DisplayName) { ExePath = facts.Path };
                apps.Add(key, app);
                access[app] = new AccessSummary();
            }
            app.ProcessIds.Add(sample.Pid);
            app.ProcessCount++;
            app.MemoryBytes += sample.PrivateWorkingSet;
            // Rules about the program itself (Windows, security software, me) cover the whole group.
            app.ProtectedReason ??= facts.RuleReason;

            AccessSummary summary = access[app];
            bool windowed = windowsByPid.TryGetValue(sample.Pid, out List<AppWindow>? windows);
            if (windowed) app.Windows.AddRange(windows!);
            summary.Note(facts, windowed);
        }

        foreach ((AppInfo app, AccessSummary summary) in access)
            app.ProtectedReason ??= summary.Reason(app.HasWindows);

        return apps.Values.OrderByDescending(a => a.MemoryBytes).ToList();
    }

    /// <summary>The group key for a process, matching <see cref="AppInfo.Key"/> (used for the foreground app).</summary>
    public static string? KeyFor(uint pid)
    {
        IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                if (Native.GetProcessPath(handle) is { } path) return path;
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return "name:" + AppNames.Normalize(p.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Private working set per process ID, for checking what's left after a close.</summary>
    public static Dictionary<uint, long> MemoryByPid() =>
        Snapshot().GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.First().PrivateWorkingSet);

    private static List<Native.ProcessSample> Snapshot()
    {
        List<Native.ProcessSample> samples = Native.SnapshotProcesses();
        if (samples.Count > 0) return samples;

        // Fallback (32-bit or the call failed): working sets, which overstate shared memory a little.
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    samples.Add(new Native.ProcessSample((uint)p.Id, p.SessionId, p.WorkingSet64, p.ProcessName + ".exe"));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return samples;
    }

    /// <summary>Roughly the Alt+Tab rule: visible, unowned, titled, not a tool window, not cloaked.</summary>
    private static bool IsAppWindow(IntPtr hwnd) =>
        Native.IsWindowVisible(hwnd)
        && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero
        && !Native.HasExtendedStyle(hwnd, Native.WS_EX_TOOLWINDOW)
        && Native.GetWindowTextLength(hwnd) > 0
        && !Native.IsCloaked(hwnd);

    /// <param name="RuleReason">Protected because of what the program is.</param>
    /// <param name="AccessReason">This process couldn't be inspected or runs elevated.</param>
    private sealed record ProcessFacts(string Name, string DisplayName, string? Path, string? RuleReason, string? AccessReason);

    private static ProcessFacts Inspect(Native.ProcessSample sample, uint myPid)
    {
        string name = AppNames.Normalize(sample.ImageName);
        string display = Path.GetFileNameWithoutExtension(sample.ImageName);

        string? rule = null;
        if (sample.Pid == myPid) rule = "that's me";
        else if (SystemProcesses.Contains(name)) rule = "Windows system process";
        else if (SecurityProcesses.Contains(name)) rule = "security software";

        IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, sample.Pid);
        if (handle == IntPtr.Zero)
            return new ProcessFacts(name, display, null, rule, "can't be inspected (access denied)");

        string? path, accessReason = null;
        try
        {
            path = Native.GetProcessPath(handle);
            if (path is null)
            {
                accessReason = "can't be inspected (no path)";
            }
            else
            {
                display = Descriptions.GetOrAdd(path, Describe) ?? display;
                if (path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) rule ??= "part of Windows";
                if (SecurityVendorFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))) rule ??= "security software";
            }

            bool? elevated = Native.IsElevated(handle);
            if (elevated is null) accessReason ??= "can't be inspected (token access denied)";
            else if (elevated.Value) accessReason ??= "runs as administrator";
        }
        finally
        {
            Native.CloseHandle(handle);
        }
        return new ProcessFacts(name, display, path, rule, accessReason);
    }

    /// <summary>
    /// Access problems only count where they matter: on processes that own windows (those are what
    /// Close talks to). Browsers' sandboxed helpers refuse inspection, and that mustn't protect the browser.
    /// </summary>
    private sealed class AccessSummary
    {
        private string? _windowedReason;
        private bool _anyInspectable;
        private string? _firstReason;

        public void Note(ProcessFacts facts, bool windowed)
        {
            if (facts.AccessReason is null) _anyInspectable = true;
            else _firstReason ??= facts.AccessReason;
            if (windowed) _windowedReason ??= facts.AccessReason;
        }

        public string? Reason(bool hasWindows) =>
            hasWindows ? _windowedReason : _anyInspectable ? null : _firstReason;
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
