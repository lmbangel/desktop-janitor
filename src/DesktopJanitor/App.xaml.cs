using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopJanitor;

/// <summary>
/// Command line:
///   --config &lt;file&gt;  use a different config file (default %APPDATA%\DesktopJanitor\config.json)
///   --report         write what's open and how the janitor would treat it to report.txt, then exit
///   --demo           show a sample question a few seconds after start (nothing is touched)
///   --memory-list    open the memory list a moment after start
///   --exit           ask a running janitor to quit (used by the installer), then exit
///   --snapshots &lt;dir&gt; render the bubble and memory list with sample data to PNGs (for the README), then exit
/// </summary>
public partial class App : Application
{
    private const string ExitSignalName = @"Local\DesktopJanitor.Exit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private JanitorWindow? _janitor;
    private CleanupService? _cleanup;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A small sprite doesn't need the GPU; skipping Direct3D roughly halves the janitor's own memory.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Info($"Unexpected error: {args.Exception}");
            args.Handled = true;
        };

        string? configPath = null, snapshotDir = null;
        bool report = false, demo = false, memoryList = false, exit = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i].ToLowerInvariant())
            {
                case "--config" when i + 1 < e.Args.Length:
                    configPath = Path.GetFullPath(e.Args[++i]);
                    break;
                case "--report":
                    report = true;
                    break;
                case "--demo":
                    demo = true;
                    break;
                case "--memory-list":
                    memoryList = true;
                    break;
                case "--exit":
                    exit = true;
                    break;
                case "--snapshots" when i + 1 < e.Args.Length:
                    snapshotDir = Path.GetFullPath(e.Args[++i]);
                    break;
            }
        }

        if (snapshotDir is not null)
        {
            Snapshots.Render(snapshotDir);
            Shutdown();
            return;
        }

        if (exit)
        {
            if (EventWaitHandle.TryOpenExisting(ExitSignalName, out EventWaitHandle? signal))
            {
                using (signal) signal.Set();
            }
            Shutdown();
            return;
        }

        ConfigStore.Load(configPath);

        if (report)
        {
            Report.Write(Report.Build(AppScanner.Scan(), _ => null, null, ConfigStore.Current), open: false);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\DesktopJanitor", out bool firstInstance);
        if (!firstInstance)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // Lets `DesktopJanitor.exe --exit` (the installer) close us cleanly, tray icon and all.
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitSignal, (_, _) => Dispatcher.InvokeAsync(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);

        var janitor = new JanitorWindow(SpriteSheet.Load(ConfigStore.Current.Sprite));
        var cleanup = new CleanupService(janitor);
        void ToggleMemoryList() => janitor.TogglePanel(() => new MemoryPanel(cleanup));

        janitor.Clicked += ToggleMemoryList;
        janitor.Show();
        cleanup.Start();
        _janitor = janitor;
        _cleanup = cleanup;
        _tray = new TrayIcon(janitor, cleanup, ToggleMemoryList, () => Shutdown());
        Log.Info($"Started: mode {ConfigStore.Current.Mode}, config {ConfigStore.FilePath}");

        if (demo) _ = DemoAsync(janitor);
        if (memoryList) _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => ToggleMemoryList(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cleanup?.Stop();
        _tray?.Dispose();
        _janitor?.Close();
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
            Log.Info("Stopped");
        }
        base.OnExit(e);
    }

    private static async Task DemoAsync(JanitorWindow janitor)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        PromptChoice choice = await janitor.AskAsync(
            "Spotify hasn't been used for 47 min",
            "It's holding 312 MB. Want me to close it? It'll still get to ask you to save anything.",
            CleanupService.QuestionButtons,
            TimeSpan.FromSeconds(30));
        janitor.Say("That was just a demo", $"You picked \"{choice}\". Nothing was touched.");
    }
}
