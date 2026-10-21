using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopJanitor;

public enum PromptChoice { Close, Trim, NotNow, Never, Dismissed }

public sealed record BubbleButton(string Label, PromptChoice Choice, bool Primary = false);

/// <summary>Comic-style bubble above the janitor. With buttons it's a question; without, a remark.</summary>
public partial class SpeechBubble : Window
{
    private readonly DispatcherTimer _timeout;
    private bool _answered;

    public SpeechBubble(string title, string? body, IReadOnlyList<BubbleButton> buttons, TimeSpan timeout)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body ?? string.Empty;
        BodyText.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;

        IsQuestion = buttons.Count > 0;
        ButtonRow.Visibility = IsQuestion ? Visibility.Visible : Visibility.Collapsed;
        foreach (BubbleButton b in buttons)
        {
            var button = new Button
            {
                Content = b.Label,
                Style = (Style)FindResource(b.Primary ? "PrimaryButton" : "SecondaryButton"),
            };
            button.Click += (_, _) => Answer(b.Choice);
            ButtonRow.Children.Add(button);
        }

        _timeout = new DispatcherTimer { Interval = timeout };
        _timeout.Tick += (_, _) => Answer(PromptChoice.Dismissed);
    }

    public event Action<PromptChoice>? Answered;

    public bool IsQuestion { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Never steal focus from what you're typing in. Remarks are click-through; questions need clicks.
        long styles = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        if (!IsQuestion) styles |= Native.WS_EX_TRANSPARENT;
        Native.AddExtendedStyle(hwnd, styles);
        _timeout.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timeout.Stop();
        base.OnClosed(e);
    }

    public void ShowNear(Rect target, Rect area)
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        PlaceAbove(target, area);
        Opacity = 1;
    }

    /// <summary>Keeps the bubble over the janitor's head with the tail pointing at him.</summary>
    public void PlaceAbove(Rect target, Rect area)
    {
        double w = ActualWidth, h = ActualHeight;
        double headX = target.Left + target.Width * 0.38;
        double left = Math.Clamp(headX - 34, area.Left + 4, Math.Max(area.Left + 4, area.Right - w - 4));
        double top = Math.Max(area.Top, target.Top + target.Height * 0.06 - h);
        if (left != Left) Left = left;
        if (top != Top) Top = top;

        double tail = Math.Clamp(headX - left - 15, 14, Math.Max(14, w - 40));
        if (Tail.Margin.Left != tail) Tail.Margin = new Thickness(tail, -2.5, 0, 0);
    }

    private void Answer(PromptChoice choice)
    {
        _timeout.Stop();
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(choice);
        Close();
    }
}
