using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopJanitor;

/// <summary>
/// Opens when you click the janitor: your RAM use and the biggest apps he's allowed to touch,
/// each with Trim and Close. Protected and deny-listed apps are never offered.
/// </summary>
public partial class MemoryPanel : Window
{
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly CleanupService _cleanup;
    private Rect _anchor = Rect.Empty;
    private Rect _area;
    private int _pending;
    private bool _closing;

    public MemoryPanel(CleanupService cleanup)
    {
        InitializeComponent();
        _cleanup = cleanup;
        Loaded += async (_, _) => await LoadAsync();
        SizeChanged += (_, _) => Place();
        // Click anywhere else and it goes away, unless an app is still closing.
        Deactivated += (_, _) =>
        {
            if (_pending == 0 && !_closing) Close();
        };
    }

    // Sample panels (README screenshots) are never shown, loaded or clicked, so they need no service.
    private MemoryPanel()
    {
        InitializeComponent();
        _cleanup = null!;
    }

    /// <summary>A panel filled with made-up rows for README screenshots (`--snapshots`). Its buttons do nothing.</summary>
    public static MemoryPanel CreateSample(
        IReadOnlyList<(string Name, string? ExePath, long Bytes, string Status, bool CanTrim, bool CanClose)> rows,
        string footer, long ramUsed, long ramTotal)
    {
        var panel = new MemoryPanel();
        long largest = Math.Max(1, rows.Max(r => r.Bytes));
        foreach (var r in rows)
        {
            (Grid row, Button trim, Button close, _) =
                panel.BuildRowCore(IconFor(r.ExePath), r.Name, r.Bytes, r.Bytes / (double)largest, r.Status);
            trim.IsEnabled = r.CanTrim;
            close.IsEnabled = r.CanClose;
            panel.Rows.Children.Add(row);
        }
        panel.FooterText.Text = footer;
        panel.ShowRam(ramUsed, ramTotal);
        return panel;
    }

    private Brush Ink => (Brush)FindResource("Ink");
    private Brush InkSoft => (Brush)FindResource("InkSoft");

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.AddExtendedStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    public void ShowNear(Rect target, Rect area)
    {
        _anchor = target;
        _area = area;
        Opacity = 0;
        Show();
        UpdateLayout();
        Place();
        Opacity = 1;
        Activate();
    }

    /// <summary>Bottom-anchored above the janitor, so it grows upwards as rows load.</summary>
    private void Place()
    {
        if (_anchor.IsEmpty) return;
        double w = ActualWidth, h = ActualHeight;
        double headX = _anchor.Left + _anchor.Width * 0.38;
        double left = Math.Clamp(headX - 34, _area.Left + 4, Math.Max(_area.Left + 4, _area.Right - w - 4));
        double top = Math.Max(_area.Top, _anchor.Top + _anchor.Height * 0.06 - h);
        if (left != Left) Left = left;
        if (top != Top) Top = top;

        double tail = Math.Clamp(headX - left - 15, 14, Math.Max(14, w - 40));
        if (Tail.Margin.Left != tail) Tail.Margin = new Thickness(tail, -2.5, 0, 0);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        Rows.Children.Clear();
        Rows.Children.Add(Note("Looking around..."));
        UpdateRam();

        // The true top users, like Task Manager sorted by Memory: protected and background ones included.
        IReadOnlyList<AppInfo> apps = await _cleanup.RefreshAppsAsync();
        var shown = apps.Take(Math.Max(1, ConfigStore.Current.MemoryListSize)).ToList();

        Rows.Children.Clear();
        long largest = shown.Count > 0 ? Math.Max(1, shown[0].MemoryBytes) : 1;
        foreach (AppInfo app in shown)
            Rows.Children.Add(BuildRow(app, largest));

        FooterText.Text = $"Top {shown.Count} of {apps.Count} programs by memory, measured like Task Manager's Memory column.";
        FooterText.Visibility = Visibility.Visible;

        UpdateRam();
        RefreshButton.IsEnabled = _pending == 0;
    }

    private UIElement BuildRow(AppInfo app, long largest)
    {
        bool isProtected = !CleanupPolicy.MayActOnManually(app);
        bool denied = CleanupPolicy.IsDenied(app, ConfigStore.Current);
        bool canClose = !isProtected && app.HasWindows;

        (Grid row, Button trim, Button close, TextBlock status) = BuildRowCore(
            IconFor(app.ExePath), app.DisplayName, app.MemoryBytes, app.MemoryBytes / (double)largest, Describe(app, isProtected, denied));
        trim.IsEnabled = !isProtected;
        close.IsEnabled = canClose;

        if (isProtected)
        {
            trim.ToolTip = close.ToolTip = $"Protected ({app.ProtectedReason}): the janitor never touches it.";
            return row;
        }
        if (!app.HasWindows) close.ToolTip = "It runs in the background with no window to close. Trim still works.";

        trim.Click += async (_, _) => await RunAsync(trim, close, canClose, status, "Trimming...", async () =>
        {
            long freed = await _cleanup.TrimFromListAsync(app);
            return ($"Trimmed {Bytes.Format(freed)}. It keeps running.", false);
        });

        // Deny-listed apps (editors, terminals, calls...) close only after a second, deliberate click.
        bool confirmed = !denied;
        close.Click += async (_, _) =>
        {
            if (!confirmed)
            {
                confirmed = true;
                close.Content = "Close anyway";
                status.Text = "It's on your deny list. Click again to close it anyway; it can still ask you to save.";
                return;
            }
            await RunAsync(trim, close, canClose, status, "Closing... it may ask you to save.", async () =>
                Describe(await _cleanup.CloseFromListAsync(app), app));
        };
        return row;
    }

    private (Grid Row, Button Trim, Button Close, TextBlock Status) BuildRowCore(
        ImageSource? iconSource, string name, long bytes, double fraction, string statusText)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Width = 20,
            Height = 20,
            Source = iconSource,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 1, 0, 0),
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        row.Children.Add(icon);

        var status = new TextBlock
        {
            Text = statusText,
            FontSize = 11.5,
            Foreground = InkSoft,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(Bar(fraction));
        info.Children.Add(status);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var memory = new TextBlock
        {
            Text = Bytes.Format(bytes),
            FontWeight = FontWeights.Bold,
            Foreground = Ink,
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(memory, 2);
        row.Children.Add(memory);

        Button trim = SmallButton("Trim", primary: false, "Move its idle memory out of RAM. It keeps running.");
        Button close = SmallButton("Close", primary: true, "Close it like clicking X. It can still ask you to save.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(trim);
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 3);
        row.Children.Add(buttons);
        return (row, trim, close, status);
    }

    /// <summary>Runs one row's action; the panel stays open until it finishes.</summary>
    private async Task RunAsync(Button trim, Button close, bool canClose, TextBlock status, string working,
        Func<Task<(string Message, bool Gone)>> action)
    {
        trim.IsEnabled = close.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        status.Text = working;
        _pending++;
        try
        {
            (string message, bool gone) = await action();
            status.Text = message;
            trim.IsEnabled = !gone;
            close.IsEnabled = !gone && canClose;
        }
        finally
        {
            _pending--;
            RefreshButton.IsEnabled = _pending == 0;
            UpdateRam();
        }
    }

    private string Describe(AppInfo app, bool isProtected, bool denied)
    {
        if (isProtected) return $"Protected: {app.ProtectedReason}";

        var parts = new List<string>();
        if (app.HasWindows)
        {
            parts.Add(app.Windows.Count == 1 ? "1 window" : $"{app.Windows.Count} windows");
            if (UsedText(_cleanup.UsageOf(app)) is { } used) parts.Add(used);
        }
        else
        {
            parts.Add(app.ProcessCount == 1 ? "Background process" : $"Background, {app.ProcessCount} processes");
        }
        if (denied) parts.Add("deny-listed");
        return string.Join(", ", parts);
    }

    private static string? UsedText(AppUsage? usage)
    {
        if (usage is null) return null;
        // Before he's seen it in front, idle time is only a lower bound ("12+ min").
        if (!usage.SeenInFront) return usage.IdleSeconds < 60 ? null : $"unused {usage.IdleMinutes:0}+ min";
        return usage.IdleSeconds < 60 ? "used just now" : $"unused {usage.IdleMinutes:0} min";
    }

    private static (string Message, bool Gone) Describe(CloseResult result, AppInfo app) => result.Outcome switch
    {
        CloseOutcome.Closed => ($"Closed. About {Bytes.Format(app.MemoryBytes)} freed.", true),
        CloseOutcome.StillOpen => ("Still open. It may be asking you to save.", false),
        CloseOutcome.InBackground => ($"Window closed, still running in the tray ({Bytes.Format(result.LeftoverBytes)}).", true),
        CloseOutcome.NotAllowed => ("Not allowed: it's protected.", true),
        _ => ("Couldn't reach its window. Try Refresh.", false),
    };

    private void UpdateRam()
    {
        (long total, long available) = Native.PhysicalMemory();
        if (total <= 0)
        {
            RamText.Text = "RAM in use: unknown";
            return;
        }
        ShowRam(total - available, total);
    }

    private void ShowRam(long used, long total)
    {
        double fraction = Math.Clamp(used / (double)total, 0, 1);
        RamText.Text = $"RAM in use: {Bytes.Format(used)} of {Bytes.Format(total)} ({fraction * 100:0}%)";
        RamUsed.Width = new GridLength(fraction, GridUnitType.Star);
        RamFree.Width = new GridLength(1 - fraction, GridUnitType.Star);
        RamUsedFill.Background = (Brush)FindResource(fraction > 0.85 ? "Warn" : "Accent");
    }

    private Button SmallButton(string label, bool primary, string tip) => new()
    {
        Content = label,
        ToolTip = tip,
        Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton"),
        Padding = new Thickness(9, 2, 9, 3),
    };

    private UIElement Bar(double fraction)
    {
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.02), GridUnitType.Star) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0), GridUnitType.Star) });
        fill.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = (Brush)FindResource("Accent") });
        return new Border
        {
            Height = 4,
            Margin = new Thickness(0, 3, 0, 3),
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("Track"),
            Child = fill,
        };
    }

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontStyle = FontStyles.Italic,
        Foreground = InkSoft,
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static ImageSource? IconFor(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (IconCache.TryGetValue(path, out ImageSource? cached)) return cached;

        ImageSource? source = null;
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                source = bitmap;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            // No icon is fine.
        }
        IconCache[path] = source;
        return source;
    }
}
