using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopJanitor;

/// <summary>
/// The downloaded exe installs itself: per-user, no admin rights. Same layout as install.ps1:
/// %LOCALAPPDATA%\Programs\DesktopJanitor, a Start menu shortcut, and an entry under
/// Settings > Apps whose Uninstall runs `DesktopJanitor.exe --uninstall`.
/// </summary>
public static class Installer
{
    public const string ExitSignalName = @"Local\DesktopJanitor.Exit";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopJanitor";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DesktopJanitor";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DesktopJanitor");

    public static string TargetPath { get; } = Path.Combine(InstallDir, "DesktopJanitor.exe");

    private static string StartMenuLink { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Desktop Janitor.lnk");

    private static string DesktopLink { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop Janitor.lnk");

    private static string ExtractedDir { get; } = Path.Combine(Path.GetTempPath(), ".net", "DesktopJanitor");

    /// <summary>Set by an uninstall; App.OnExit then deletes the install folder once this process is gone.</summary>
    public static bool RemoveFolderOnExit { get; private set; }

    /// <summary>A published single-file exe has no assembly path; dev builds (dotnet run, bin\) do.</summary>
#pragma warning disable IL3000 // An empty Location in single-file builds is exactly what we're testing for.
    public static bool IsSingleFileBuild => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    public static bool IsRunningInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? string.Empty), TargetPath, StringComparison.OrdinalIgnoreCase);

    public static Version ThisVersion
    {
        get
        {
            Version v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static Version? InstalledVersion
    {
        get
        {
            if (!File.Exists(TargetPath)) return null;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(TargetPath);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
    }

    public static bool HasDesktopShortcut => File.Exists(DesktopLink);

    public static bool StartsWithWindows
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
    }

    public static async Task InstallAsync(bool startWithWindows, bool desktopShortcut, IProgress<string> progress)
    {
        progress.Report("Asking the janitor on duty to clock off...");
        await StopOtherInstancesAsync();

        progress.Report("Moving his things in...");
        Directory.CreateDirectory(InstallDir);
        await CopyWithRetryAsync(Environment.ProcessPath!, TargetPath);
        // The copy is a new local file: don't carry over the "downloaded from the internet" mark,
        // or Windows would show its SmartScreen warning again every time he starts.
        DeleteFile(TargetPath + ":Zone.Identifier");

        progress.Report("Putting up signs...");
        CreateShortcut(StartMenuLink);
        if (desktopShortcut) CreateShortcut(DesktopLink);
        else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "Desktop Janitor");
            key.SetValue("DisplayVersion", ThisVersion.ToString());
            key.SetValue("Publisher", "Desktop Janitor");
            key.SetValue("DisplayIcon", TargetPath);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{TargetPath}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(TargetPath).Length / 1024), RegistryValueKind.DWord);
        }

        using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (startWithWindows) run.SetValue(RunValue, $"\"{TargetPath}\"");
            else if (run.GetValue(RunValue) is not null) run.DeleteValue(RunValue);
        }

        Log.Info($"Installed {ThisVersion} to {InstallDir} (start with Windows: {startWithWindows}, desktop shortcut: {desktopShortcut})");
        progress.Report("Starting his shift...");
        LaunchInstalled();
    }

    public static async Task UninstallAsync(bool removeSettings, IProgress<string> progress)
    {
        // Settings > Apps may start us inside the install folder; don't hold it open as our working directory.
        Environment.CurrentDirectory = Path.GetTempPath();
        progress.Report("Asking the janitor to clock off...");
        await StopOtherInstancesAsync();

        progress.Report("Taking down the signs...");
        foreach (string link in new[] { StartMenuLink, DesktopLink })
        {
            if (File.Exists(link)) File.Delete(link);
        }

        using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            // Only remove a start-up entry that points at the installed copy.
            if (run?.GetValue(RunValue) is string value && value.Contains(InstallDir, StringComparison.OrdinalIgnoreCase))
                run.DeleteValue(RunValue);
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        Log.Info($"Uninstalled (settings removed: {removeSettings})");
        if (removeSettings)
        {
            foreach (string dir in new[] { Path.GetDirectoryName(ConfigStore.DefaultPath)!, Log.Folder })
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
        RemoveFolderOnExit = true;
    }

    /// <summary>Called as the uninstaller exits: a hidden helper deletes the folder a moment after we're gone.</summary>
    public static void StartFolderRemoval()
    {
        static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
        // Retry for a while: antivirus or the search indexer often holds a folder for a moment after its files go.
        string script =
            "Start-Sleep -Seconds 2; " +
            $"foreach ($d in {Quote(InstallDir)}, {Quote(ExtractedDir)}) {{ " +
            "for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $d); $i++) { " +
            "Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 } }";
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(), // not inside the folder it's deleting
        });
    }

    public static void LaunchInstalled() =>
        // Through Explorer, like a Start menu launch: never inherits elevation from this process.
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TargetPath}\"") { UseShellExecute = false });

    private static async Task StopOtherInstancesAsync()
    {
        int me = Environment.ProcessId;
        bool OthersRunning() => Process.GetProcessesByName("DesktopJanitor").Any(p => p.Id != me);
        if (!OthersRunning()) return;

        if (EventWaitHandle.TryOpenExisting(ExitSignalName, out EventWaitHandle? signal))
        {
            using (signal) signal.Set();
        }
        for (int i = 0; i < 40 && OthersRunning(); i++) await Task.Delay(250);

        // Older builds don't listen for the signal. The janitor holds no data, so stopping it is safe.
        foreach (Process p in Process.GetProcessesByName("DesktopJanitor").Where(p => p.Id != me))
        {
            using (p)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }
        }
    }

    private static async Task CopyWithRetryAsync(string from, string to)
    {
        // Right after the old janitor quits, antivirus (or Windows) can keep its exe open for a few seconds.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 30)
            {
                await Task.Delay(1000);
            }
        }
    }

    private static void CreateShortcut(string path)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = TargetPath;
            link.WorkingDirectory = InstallDir;
            link.IconLocation = $"{TargetPath},0";
            link.Description = "A little janitor who sweeps above your taskbar and tidies idle apps";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFile(string path);
}
