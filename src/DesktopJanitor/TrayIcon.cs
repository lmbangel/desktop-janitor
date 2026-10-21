using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DesktopJanitor;

/// <summary>The janitor's window is click-through, so everything you control lives in the tray menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DesktopJanitor";

    private readonly Forms.NotifyIcon _icon;
    private readonly JanitorWindow _janitor;
    private readonly Forms.ToolStripMenuItem _breakItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Dictionary<CleanupMode, Forms.ToolStripMenuItem> _modeItems = new();

    public TrayIcon(JanitorWindow janitor, CleanupService cleanup, Action showMemoryList, Action exit)
    {
        _janitor = janitor;

        var modes = new Forms.ToolStripMenuItem("Cleanup mode");
        AddMode(modes, CleanupMode.Ask, "Ask before closing idle apps");
        AddMode(modes, CleanupMode.TrimOnly, "Only trim memory, never close");
        AddMode(modes, CleanupMode.Off, "Off, just sweep");

        _breakItem = new Forms.ToolStripMenuItem("Take a break", null, (_, _) => _janitor.OnBreak = !_janitor.OnBreak);
        _startupItem = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Desktop Janitor") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Show memory list (or click him)", null, (_, _) => showMemoryList());
        menu.Items.Add("Check for idle apps now", null, async (_, _) => await cleanup.CheckNowAsync());
        menu.Items.Add("Sweep memory now (trim background apps)", null, async (_, _) => await cleanup.SweepMemoryNowAsync());
        menu.Items.Add("What's open? (report)", null, async (_, _) => await cleanup.ShowReportAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(modes);
        menu.Items.Add(_breakItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open config file", null, (_, _) => Shell.Open(ConfigStore.FilePath));
        menu.Items.Add("Reload config", null, (_, _) => ReloadConfig());
        menu.Items.Add("Open log", null, (_, _) => Shell.Open(Log.FilePath));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        menu.Opening += (_, _) => RefreshChecks();

        _icon = new Forms.NotifyIcon
        {
            Text = "Desktop Janitor",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += async (_, _) => await cleanup.ShowReportAsync();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void AddMode(Forms.ToolStripMenuItem parent, CleanupMode mode, string label)
    {
        var item = new Forms.ToolStripMenuItem(label, null, (_, _) =>
        {
            ConfigStore.Current.Mode = mode;
            ConfigStore.Save();
            Log.Info($"Mode set to {mode}");
        });
        _modeItems[mode] = item;
        parent.DropDownItems.Add(item);
    }

    private void RefreshChecks()
    {
        foreach ((CleanupMode mode, Forms.ToolStripMenuItem item) in _modeItems)
            item.Checked = ConfigStore.Current.Mode == mode;
        _breakItem.Checked = _janitor.OnBreak;
        _startupItem.Checked = IsStartupEnabled();
    }

    private void ReloadConfig()
    {
        ConfigStore.Load();
        _janitor.ApplyConfig();
        _janitor.Say("Config reloaded", $"Mode: {ConfigStore.Current.Mode}. A new sprite sheet needs a restart.");
    }

    private static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    private static void ToggleStartup()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(RunValue) is not null)
        {
            key.DeleteValue(RunValue);
            Log.Info("Removed from Windows start-up");
        }
        else
        {
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            Log.Info($"Added to Windows start-up: {Environment.ProcessPath}");
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/janitor.ico"));
        using Stream stream = resource!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}

public static class Shell
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No app registered for the file type (common for .json/.log).
            Process.Start("notepad.exe", $"\"{path}\"");
        }
    }
}
