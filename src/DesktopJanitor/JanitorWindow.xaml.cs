using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopJanitor;

/// <summary>
/// The janitor himself: a transparent, topmost window that walks along the top edge of
/// the taskbar, stops now and then to sweep, and fades out at the right edge to start
/// again from the left. Only his drawn pixels catch the mouse; click him for the memory list.
/// </summary>
public partial class JanitorWindow : Window
{
    private enum State { Walking, Sweeping, Standing }

    private const double FadeSeconds = 0.8;

    private readonly SpriteSheet _sprites;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();

    private IntPtr _hwnd;
    private double _dpi = 1;
    private State _state = State.Walking;
    private double _lastTime;
    private double _stateStartedAt;
    private double _stateEndsAt;
    private double _nextSweepAt = 5;
    private double _nextHousekeeping;
    private double _x = double.NaN;
    private double _fade;
    private int _fadeDirection = +1;
    private bool _hiddenForFullscreen;
    private bool _onBreak;
    private bool _hoverPaused;
    private ImageSource? _shownFrame;
    private SpeechBubble? _bubble;
    private MemoryPanel? _panel;

    public JanitorWindow(SpriteSheet sprites)
    {
        InitializeComponent();
        _sprites = sprites;
        Opacity = 0;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Step();
        MouseEnter += (_, _) => OnHover(true);
        MouseLeave += (_, _) => OnHover(false);
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
    }

    /// <summary>You clicked on him.</summary>
    public event Action? Clicked;

    /// <summary>On a break he stands still and asks nothing.</summary>
    public bool OnBreak
    {
        get => _onBreak;
        set
        {
            _onBreak = value;
            // An open question or the memory list keeps him standing; he picks the break up when it closes.
            if (_bubble is not { IsQuestion: true } && _panel is null) EnterState(value ? State.Standing : State.Walking);
        }
    }

    /// <summary>True when a question can be asked right now.</summary>
    public bool CanTalk => !_onBreak && !_hiddenForFullscreen && _fadeDirection == 0 && _panel is null && _bubble is not { IsQuestion: true };

    private Rect Bounds => new(Left, Top, Width, Height);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // No Alt+Tab entry and never takes focus. Click-through (or not) is set in ApplyConfig.
        Native.AddExtendedStyle(_hwnd, Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        ApplyConfig();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        CloseBubble();
        _panel?.Close();
        base.OnClosed(e);
    }

    /// <summary>Size and clickability from the current config (also used by "Reload config").</summary>
    public void ApplyConfig()
    {
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // A whole number of device pixels per art pixel keeps the pixel art crisp at any display scaling.
        double devicePixels = Math.Max(1, Math.Round(ConfigStore.Current.Scale * _dpi));
        Width = _sprites.FrameWidth * devicePixels / _dpi;
        Height = _sprites.FrameHeight * devicePixels / _dpi;

        // A layered window only gets the mouse where its pixels aren't fully transparent,
        // so without WS_EX_TRANSPARENT just his body is clickable; the box around him isn't.
        bool clickable = ConfigStore.Current.Clickable;
        Native.SetExtendedStyle(_hwnd, Native.WS_EX_TRANSPARENT, on: !clickable);
        Cursor = clickable ? Cursors.Hand : null;
    }

    /// <summary>Stops, shows a question bubble and returns the answer (Dismissed on timeout).</summary>
    public Task<PromptChoice> AskAsync(string title, string body, IReadOnlyList<BubbleButton> buttons, TimeSpan timeout)
    {
        CloseBubble();
        EnterState(State.Standing);

        var bubble = new SpeechBubble(title, body, buttons, timeout);
        // Async continuations: the caller resumes only after the bubble has fully closed.
        var answer = new TaskCompletionSource<PromptChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        bubble.Answered += choice => answer.TrySetResult(choice);
        bubble.Closed += (_, _) =>
        {
            answer.TrySetResult(PromptChoice.Dismissed);
            if (_bubble != bubble) return;
            _bubble = null;
            if (_panel is null) EnterState(_onBreak ? State.Standing : State.Walking);
        };
        ShowBubble(bubble);
        return answer.Task;
    }

    /// <summary>Short remark that travels with him; never replaces an open question or the memory list.</summary>
    public void Say(string title, string? body = null, double seconds = 6)
    {
        Log.Info($"Janitor says: {title} {body}".TrimEnd());
        if (_bubble is { IsQuestion: true } || _panel is not null || _hiddenForFullscreen) return;

        CloseBubble();
        var bubble = new SpeechBubble(title, body, Array.Empty<BubbleButton>(), TimeSpan.FromSeconds(seconds));
        bubble.Closed += (_, _) =>
        {
            if (_bubble == bubble) _bubble = null;
        };
        ShowBubble(bubble);
    }

    /// <summary>Opens the memory list above him, or closes it if it's already open.</summary>
    public void TogglePanel(Func<MemoryPanel> create)
    {
        if (_panel is not null)
        {
            _panel.Close();
            return;
        }
        if (_hiddenForFullscreen) return;

        CloseBubble(); // an open question counts as unanswered
        EnterState(State.Standing);

        MemoryPanel panel = create();
        panel.Closed += (_, _) =>
        {
            if (_panel != panel) return;
            _panel = null;
            if (!_hoverPaused) EnterState(_onBreak ? State.Standing : State.Walking);
        };
        _panel = panel;
        panel.ShowNear(Bounds, SystemParameters.WorkArea);
    }

    private void OnHover(bool over)
    {
        // Stand still under the mouse so he's easy to click.
        if (over)
        {
            if (ConfigStore.Current.Clickable && _state != State.Standing)
            {
                _hoverPaused = true;
                EnterState(State.Standing);
            }
            return;
        }

        if (!_hoverPaused) return;
        _hoverPaused = false;
        if (_panel is null && _bubble is not { IsQuestion: true } && !_onBreak) EnterState(State.Walking);
    }

    private void ShowBubble(SpeechBubble bubble)
    {
        _bubble = bubble;
        bubble.ShowNear(Bounds, SystemParameters.WorkArea);
    }

    private void CloseBubble()
    {
        var bubble = _bubble;
        _bubble = null;
        bubble?.Close();
    }

    private void EnterState(State state, double endsAt = 0)
    {
        double now = _clock.Elapsed.TotalSeconds;
        _state = state;
        _stateStartedAt = now;
        _stateEndsAt = endsAt;
        if (state == State.Walking)
            _nextSweepAt = now + 4 + _random.NextDouble() * 8;
    }

    private void Step()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastTime, 0.1);
        _lastTime = now;

        JanitorConfig config = ConfigStore.Current;
        Rect area = SystemParameters.WorkArea;

        if (now >= _nextHousekeeping)
        {
            Housekeeping(config);
            _nextHousekeeping = now + 2;
        }

        if (double.IsNaN(_x)) _x = area.Left;

        switch (_state)
        {
            case State.Walking:
                _x += config.WalkSpeed * dt;
                if (config.SweepBreaks && _fadeDirection == 0 && now >= _nextSweepAt)
                    EnterState(State.Sweeping, now + 1.5 + _random.NextDouble() * 2);
                break;
            case State.Sweeping:
                if (now >= _stateEndsAt) EnterState(State.Walking);
                break;
        }

        // Close to the right edge: fade out, hop back to the left, fade in.
        // A fade-out pauses while he stands, so he never vanishes from under your mouse.
        if (_fadeDirection == 0 && _state == State.Walking && _x + Width >= area.Right - config.WalkSpeed * FadeSeconds)
            _fadeDirection = -1;
        if (_fadeDirection == +1 || (_fadeDirection == -1 && _state == State.Walking))
        {
            _fade += _fadeDirection * dt / FadeSeconds;
            if (_fade <= 0)
            {
                _fade = 0;
                _x = area.Left;
                _fadeDirection = +1;
            }
            else if (_fade >= 1)
            {
                _fade = 1;
                _fadeDirection = 0;
            }
        }
        _x = Math.Clamp(_x, area.Left, Math.Max(area.Left, area.Right - Width));

        Animation animation = _state switch
        {
            State.Walking => _sprites.Walk,
            State.Sweeping => _sprites.Sweep,
            _ => _sprites.Idle,
        };
        ImageSource frame = animation.FrameAt(now - _stateStartedAt);
        if (!ReferenceEquals(frame, _shownFrame))
        {
            Sprite.Source = frame;
            _shownFrame = frame;
        }

        // Snap to device pixels so the sprite doesn't shimmer while moving.
        double left = Math.Round(_x * _dpi) / _dpi;
        double top = Math.Round((area.Bottom - Height) * _dpi) / _dpi;
        if (left != Left) Left = left;
        if (top != Top) Top = top;

        double opacity = _hiddenForFullscreen ? 0 : _fade;
        if (opacity != Opacity) Opacity = opacity;

        _bubble?.PlaceAbove(Bounds, area);
    }

    private void Housekeeping(JanitorConfig config)
    {
        bool hide = config.HideInFullscreen && Native.IsSomethingFullscreen();
        if (hide != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = hide;
            if (_bubble is not null) _bubble.Opacity = hide ? 0 : 1;
        }

        // Other topmost windows (the taskbar included) can end up above us; climb back on top.
        if (!hide) Native.KeepTopmost(_hwnd);
    }
}
