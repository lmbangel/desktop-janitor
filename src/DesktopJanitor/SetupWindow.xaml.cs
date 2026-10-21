using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DesktopJanitor;

public enum SetupMode { Install, Update, Uninstall }

/// <summary>
/// What a non-developer sees when they double-click the downloaded exe (Install / Update), or
/// choose Uninstall in Settings > Apps. Per-user, no admin rights; the work is in Installer.
/// </summary>
public partial class SetupWindow : Window
{
    private const string InstallFootnote =
        "Installs just for you, in your user Programs folder, with no admin rights needed. Remove him any time from Settings > Apps.";

    private readonly SetupMode _mode;
    private readonly SpriteSheet _sprites;
    private readonly DispatcherTimer _animation;
    private Animation _current;
    private DateTime _animationStart = DateTime.Now;
    private Action? _primary;
    private Action? _secondary;
    private bool _busy;
    private bool _handedOver;

    public SetupWindow(SetupMode mode)
    {
        InitializeComponent();
        _mode = mode;
        _sprites = SpriteSheet.Load(new SpriteConfig());
        _current = _sprites.Idle;
        Sprite.Source = _current.Frames[0];
        _animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _animation.Tick += (_, _) => Sprite.Source = _current.FrameAt((DateTime.Now - _animationStart).TotalSeconds);
        Loaded += (_, _) => _animation.Start();
        Closed += (_, _) =>
        {
            _animation.Stop();
            if (!_handedOver) Application.Current.Shutdown();
        };
        ShowIntro();
    }

    /// <summary>"Just try him": run from wherever the file is, without installing.</summary>
    public event Action? RunWithoutInstalling;

    /// <summary>The install screen for README screenshots (`--snapshots`); closing it doesn't end the app.</summary>
    public static SetupWindow CreateSample() => new(SetupMode.Install) { _handedOver = true };

    private void ShowIntro()
    {
        Version mine = Installer.ThisVersion;
        switch (_mode)
        {
            case SetupMode.Install:
                Present("Hi! I'm the Desktop Janitor.",
                    "I sweep along the top of your taskbar, and when an app has sat unused for a while, I ask whether to " +
                    "tidy it away. I never close anything without asking. Shall I move in?",
                    startWithWindows: false, desktopShortcut: true, removeSettings: null,
                    ("Install", Install), ("Just try him", JustTry), InstallFootnote);
                break;

            case SetupMode.Update:
                Version installed = Installer.InstalledVersion ?? new Version(0, 0, 0);
                (string heading, string body, string action) = mine.CompareTo(installed) switch
                {
                    > 0 => ($"A new uniform: v{mine}", $"You have v{installed} installed. Update him? Your settings stay as they are.", "Update"),
                    0 => ($"I'm already installed (v{mine}).", "Reinstall from this file, or just start the one that's installed?", "Reinstall"),
                    _ => ($"You have a newer janitor (v{installed}).", $"This file is v{mine}. Keep your newer one, or replace it anyway?", "Replace anyway"),
                };
                Present(heading, body, Installer.StartsWithWindows, Installer.HasDesktopShortcut, removeSettings: null,
                    (action, Install), ("Start installed one", StartInstalled), InstallFootnote);
                break;

            case SetupMode.Uninstall:
                Present("Taking off the apron?",
                    "This removes Desktop Janitor from this PC: the app, its shortcuts and its start-up entry.",
                    startWithWindows: null, desktopShortcut: null, removeSettings: false,
                    ("Remove", Uninstall), ("Keep him", CloseApp), "Your settings and log stay unless you tick the box.");
                break;
        }
    }

    private void Present(string heading, string body, bool? startWithWindows, bool? desktopShortcut, bool? removeSettings,
        (string Label, Action Run) primary, (string Label, Action Run)? secondary, string footnote)
    {
        Heading.Text = heading;
        Body.Text = body;
        SetOption(StartWithWindowsBox, startWithWindows);
        SetOption(DesktopShortcutBox, desktopShortcut);
        SetOption(RemoveSettingsBox, removeSettings);

        PrimaryAction.Content = primary.Label;
        _primary = primary.Run;
        SecondaryAction.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryAction.Content = secondary?.Label;
        _secondary = secondary?.Run;

        Footnote.Text = footnote;
        Footnote.Visibility = string.IsNullOrEmpty(footnote) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetOption(System.Windows.Controls.CheckBox box, bool? value)
    {
        box.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        box.IsChecked = value == true;
    }

    private void SetBusy(bool busy, Animation animation)
    {
        _busy = busy;
        PrimaryAction.IsEnabled = SecondaryAction.IsEnabled = CloseButton.IsEnabled = Options.IsEnabled = !busy;
        Status.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _current = animation;
        _animationStart = DateTime.Now;
    }

    private async void Install()
    {
        SetBusy(true, _sprites.Sweep);
        var progress = new Progress<string>(text => Status.Text = text);
        try
        {
            await Installer.InstallAsync(StartWithWindowsBox.IsChecked == true, DesktopShortcutBox.IsChecked == true, progress);
            SetBusy(false, _sprites.Walk);
            Present("All set!",
                "Look just above your taskbar: he's starting his shift. You'll find him in the Start menu as " +
                "Desktop Janitor, and his face in the tray by the clock. Click him to see what's using your memory.",
                null, null, null, ("Done", CloseApp), null, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Info($"Install failed: {ex}");
            SetBusy(false, _sprites.Idle);
            Present("Hmm, that didn't work.", ex.Message, null, null, null, ("Close", CloseApp), null,
                "If his old copy was still closing, wait a moment and try again.");
        }
    }

    private async void Uninstall()
    {
        SetBusy(true, _sprites.Sweep);
        var progress = new Progress<string>(text => Status.Text = text);
        try
        {
            await Installer.UninstallAsync(RemoveSettingsBox.IsChecked == true, progress);
            SetBusy(false, _sprites.Walk);
            Present("He's clocked off for good.", "Thanks for letting him tidy up. Bye!", null, null, null,
                ("Close", CloseApp), null, string.Empty);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) => CloseApp();
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Info($"Uninstall failed: {ex}");
            SetBusy(false, _sprites.Idle);
            Present("Hmm, that didn't work.", ex.Message, null, null, null, ("Close", CloseApp), null, string.Empty);
        }
    }

    private void JustTry()
    {
        _handedOver = true;
        Close();
        RunWithoutInstalling?.Invoke();
    }

    private void StartInstalled()
    {
        Installer.LaunchInstalled();
        CloseApp();
    }

    private void CloseApp() => Close(); // Closed shuts the app down

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => _primary?.Invoke();

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => _secondary?.Invoke();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
