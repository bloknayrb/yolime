using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Yolime.Core;
using Yolime.Core.Feedback;
using Yolime.Core.Output;
using Yolime.Core.Overlay;
using Yolime.Core.Settings;

namespace Yolime.App.Shell;

/// <summary>
/// The notch, wired: run events in (from any thread), <see cref="OverlayModel"/> on the UI thread, the view rendered
/// into the island. It also routes notices: onto the card normally, to a tray balloon while a full-screen app would
/// hide the notch. Nothing here logs or records view text.
/// </summary>
internal sealed partial class OverlayController : INoticeSink, IDisposable
{
    private const double MaxWidthDip = 560;

    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _timer;
    private readonly OverlayModel _model;
    private readonly Func<YolimeSettings> _settings;
    private readonly INoticeSink _balloons;
    private readonly UISettings _uiSettings = new();
    private readonly OverlayWindow _notch;
    private readonly OverlayWindow _pill;

    private readonly Border _root = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(1) };
    private readonly FontIcon _icon = new() { FontSize = 16, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _elapsed = new() { FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _level = new() { Height = 3, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ScrollViewer _previewScroll = new() { MaxHeight = 260, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _previewText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
    // Narrator reads this, not the visible text: a peer for an element that only exists to announce.
    // Narration goes out as a notification event with its own text, so this block needs no size. Width matters: it is
    // laid out in the card, and Render measures before Announce sets the new text, so an unbounded width sized each card
    // to the previous card's narration (a one-line "Copied" card at full width).
    private readonly TextBlock _narrator = new() { Opacity = 0, Height = 0, Width = 1, IsHitTestVisible = false };

    private OverlayView _shown = OverlayView.Hidden;
    private readonly Windows.Foundation.TypedEventHandler<UISettings, object> _themeChanged;
    private bool _disposed;

    public OverlayController(DispatcherQueue ui, Func<YolimeSettings> settings, INoticeSink balloons)
    {
        _ui = ui;
        _settings = settings;
        _balloons = balloons;
        _model = new OverlayModel((float)settings().SilenceRms);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_elapsed, 2);
        header.Children.Add(_icon);
        header.Children.Add(_title);
        header.Children.Add(_elapsed);
        _previewScroll.Content = _previewText;
        AutomationProperties.SetLiveSetting(_narrator, AutomationLiveSetting.Polite);

        var stack = new StackPanel { MaxWidth = MaxWidthDip - 34, MinWidth = 220 };
        foreach (var child in new UIElement[] { header, _detail, _level, _previewScroll, _buttons, _narrator }) stack.Children.Add(child);
        _root.Child = stack;
        _root.PointerEntered += (_, _) => Post(new PointerOver(true));
        _root.PointerExited += (_, _) => Post(new PointerOver(false));

        // It copies the raw transcript; "Use raw" read as replacing the paste.
        var pillButton = Button("Copy raw", () => { if (_shown.HistoryId is { } id) UseRaw?.Invoke(id); Post(new UserTyped()); });
        var pillRoot = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Child = pillButton };

        _notch = new OverlayWindow("yolime-overlay", _root, clickThrough: true);
        _pill = new OverlayWindow("yolime-overlay-pill", pillRoot, clickThrough: false);
        ApplyTheme(pillRoot);
        ApplyTheme(_root);
        ApplyCaptureSetting();

        _themeChanged = (_, _) => _ui.TryEnqueue(() =>
        {
            if (_disposed) return;
            ApplyTheme(_root);
            ApplyTheme(pillRoot);
        });
        _uiSettings.ColorValuesChanged += _themeChanged;

        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) =>
        {
#if YOLIME_E2E
            if (TestFreezeTicks) return;
#endif
            Apply(new Tick(NowMs));
        };
    }

    /// <summary>The notch's window, for the E2E probe and the hook's "is this click on the card" test.</summary>
    public OverlayWindow Notch => _notch;

    public event Action<long>? Retry;
    public event Action<long>? UseRaw;
    public event Action? OpenMicSettings;
    public event Action? OpenUpdateLog;
    public event Action<Yolime.Core.Output.PreviewChoice>? PreviewChosen;

    /// <summary>The state and buttons after every change (E2E records these; never the text).</summary>
    public event Action<OverlayView>? ViewChanged;

    public static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>From any thread. Inputs are applied in the order they're posted.</summary>
    public void Post(OverlayInput input)
    {
        // While a full-screen app hides the notch, its notices go to a balloon as well (run results and the rest).
        if (input is RunFinished { Notice: { } n } && !CanShow()) _balloons.Show(n);
        if (input is NoticeOnly only && !CanShow()) _balloons.Show(only.Notice);
        if (input is UpdatingEnded { Notice: { } ended } && !CanShow()) _balloons.Show(ended);
        _ui.TryEnqueue(() => Apply(input));
    }

    /// <summary>Executor and coordinator notices: onto the card, or a balloon while the notch can't be seen.</summary>
    public void Show(Notice notice) => Post(new NoticeOnly(notice, NowMs));

#if YOLIME_E2E
    /// <summary>
    /// E2E only, numbers only: where the card's content actually landed in the island's own layout, against the room
    /// the card has. A StackPanel stacks its children at their full heights even past its bounds, so a lowest child
    /// ending below the room, or a button row wider than the card, means something is cut off.
    /// "lowestBottom room buttonsRight cardWidth rasterizationScale textScale", in DIP.
    /// </summary>
    internal string FitProbe()
    {
        var stack = (StackPanel)_root.Child;
        var lowest = 0.0;
        var each = new List<string>();
        var c = System.Globalization.CultureInfo.InvariantCulture;
        for (var i = 0; i < stack.Children.Count; i++)
        {
            // The narrator line is laid out with a height despite Height = 0, but it's invisible: only what can be seen counts.
            if (stack.Children[i] is not FrameworkElement child || child.Visibility != Visibility.Visible || child.ActualHeight <= 0 || child.Opacity == 0) continue;
            var top = child.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var bottom = top + child.ActualHeight;
            each.Add($"{i}:{top.ToString("0.0", c)}+{child.ActualHeight.ToString("0.0", c)}");
            lowest = Math.Max(lowest, bottom);
        }
        // DesiredSize is clamped to the space offered, so the row's real extent is its last button's right edge.
        var rightmost = 0.0;
        foreach (var b in _buttons.Children.OfType<FrameworkElement>())
            rightmost = Math.Max(rightmost, b.TransformToVisual(stack).TransformPoint(new Windows.Foundation.Point(b.ActualWidth, 0)).X);
        var room = _root.ActualHeight - _root.Padding.Bottom - _root.BorderThickness.Bottom;
        return string.Join(' ', lowest.ToString("0.00", c), room.ToString("0.00", c), rightmost.ToString("0.00", c),
            stack.ActualWidth.ToString("0.00", c), (_root.XamlRoot?.RasterizationScale ?? 0).ToString("0.00", c), _uiSettings.TextScaleFactor.ToString("0.00", c),
            _root.ActualHeight.ToString("0.0", c) + ";" + string.Join(',', each));
    }

    /// <summary>
    /// E2E only: while set, the 100 ms timer's ticks are dropped, so a staged card stays as it is for the UI tour
    /// (Render restarts the timer on every show, so stopping it wouldn't hold). Ticks the tour posts still apply.
    /// </summary>
    internal static volatile bool TestFreezeTicks;

    /// <summary>E2E only: what <see cref="ApplyTheme"/> last chose for the notch and the pill, as "theme r,g,b".</summary>
    internal string? TestNotchTheme, TestPillTheme;

    /// <summary>E2E only: "hide from screen capture" on both windows, without the setting. False if Windows refused either.</summary>
    internal bool TestSetCaptureHidden(bool on) => _notch.SetExcludeFromCapture(on) & _pill.SetExcludeFromCapture(on);

    /// <summary>
    /// E2E only, on the UI thread: applies an input now, skipping <see cref="Post"/>'s balloon (the Sandbox reports the
    /// notch as unseeable, so every staged card would also queue a toast over the tour's later images).
    /// </summary>
    internal void TestApply(OverlayInput input) => Apply(input);
#endif

    /// <summary>The bounds of whatever card is showing, for the hook to tell a click on it from a click elsewhere.</summary>
    public bool IsOnCard(int x, int y)
    {
        var b = _notch.Bounds;
        return _notch.Visible && x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom;
    }

    private void Apply(OverlayInput input)
    {
        var view = _model.Apply(input);
        if (ReferenceEquals(view, _shown) || view == _shown) return;
        Render(view);
    }

    private void Render(OverlayView view)
    {
        var previous = _shown;
        _shown = view;
        ViewChanged?.Invoke(view);

        if (view.State is OverlayState.Hidden or OverlayState.UseRaw)
        {
            _notch.Hide();
            _notch.SetClickThrough(true);
        }
        else
        {
            _icon.Glyph = Glyph(view);
            _title.Text = view.Title;
            _elapsed.Text = view.Elapsed ?? "";
            _elapsed.Visibility = view.Elapsed is null ? Visibility.Collapsed : Visibility.Visible;
            _detail.Text = view.Detail ?? "";
            _detail.Visibility = string.IsNullOrEmpty(view.Detail) ? Visibility.Collapsed : Visibility.Visible;
            _level.Visibility = view.State == OverlayState.Listening ? Visibility.Visible : Visibility.Collapsed;
            _level.Value = Math.Min(1, Math.Sqrt(view.Level) * 4);
            _previewText.Text = view.Preview ?? "";
            _previewScroll.Visibility = view.Preview is null ? Visibility.Collapsed : Visibility.Visible;
            if (!view.Buttons.SequenceEqual(previous.Buttons) || previous.State != view.State) BuildButtons(view);
            _notch.SetClickThrough(!view.Clickable);
            // Re-measure whenever the shape may have changed; position is chosen fresh on each show.
            _notch.Show(_settings().OverlayPosition == OverlayPosition.BottomCentre, MaxWidthDip);
        }

        if (view.OfferUseRaw) _pill.Show(false, 200, _notch.Visible ? _notch.Bounds : PillAnchor());
        else _pill.Hide();

        if (view.Narration is { } say && say != previous.Narration) Announce(say, view.NarrateUrgently);

        if (view.State == OverlayState.Hidden) _timer.Stop();
        else if (!_timer.IsRunning) _timer.Start();
    }

    private (int, int, int, int) PillAnchor()
    {
        // The notch has gone (Done timed out) but the pill stays where the notch was.
        var b = _notch.Bounds;
        return (b.Left, b.Top, b.Right, b.Bottom);
    }

    private void BuildButtons(OverlayView view)
    {
        _buttons.Children.Clear();
        foreach (var b in view.Buttons)
        {
            _buttons.Children.Add(b switch
            {
                // Named with its key, as the preview's buttons are: a tap of the hold key retries too.
                OverlayButton.Retry => Button($"Retry (tap {Yolime.Core.Hotkeys.HoldKeyRules.Name(_settings().HoldKeyVk)})", () => { if (_shown.HistoryId is { } id) Retry?.Invoke(id); Post(new Dismissed()); }),
                OverlayButton.OpenSettings => Button("Open privacy settings", () => { OpenMicSettings?.Invoke(); Post(new Dismissed()); }),
                OverlayButton.OpenLog => Button("Open the log", () => { OpenUpdateLog?.Invoke(); Post(new Dismissed()); }),
                OverlayButton.Insert => Button("Insert (Enter)", () => PreviewChosen?.Invoke(Yolime.Core.Output.PreviewChoice.Insert)),
                OverlayButton.Copy => Button("Copy (C)", () => PreviewChosen?.Invoke(Yolime.Core.Output.PreviewChoice.Copy)),
                OverlayButton.Discard => Button("Discard (Esc)", () => PreviewChosen?.Invoke(Yolime.Core.Output.PreviewChoice.Discard)),
                _ => Button("Dismiss", () => Post(new Dismissed())),
            });
        }
        _buttons.Visibility = view.Buttons.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Never focusable: a click must not move keyboard focus out of the user's app.</summary>
    private static Button Button(string label, Action onClick)
    {
        var button = new Button { Content = label, AllowFocusOnInteraction = false, IsTabStop = false, Padding = new Thickness(10, 4, 10, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A bare hold-key tap: retries the run on a retryable error card (spec: "a tap of the hotkey").</summary>
    public void HotkeyTapped() => _ui.TryEnqueue(() =>
    {
        if (_shown.State == OverlayState.Error && _shown.Buttons.Contains(OverlayButton.Retry) && _shown.HistoryId is { } id)
        {
            Retry?.Invoke(id);
            Apply(new Dismissed());
        }
    });

    public void PreviewScroll(int lines)
    {
        _ui.TryEnqueue(() => _previewScroll.ChangeView(null, Math.Max(0, _previewScroll.VerticalOffset + lines * 20), null));
    }

    private void Announce(string text, bool urgent)
    {
        _narrator.Text = text;
        var peer = FrameworkElementAutomationPeer.FromElement(_narrator) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_narrator);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
            urgent ? AutomationNotificationProcessing.ImportantAll : AutomationNotificationProcessing.MostRecent, text, "yolime-overlay");
    }

    private static string Glyph(OverlayView view) => view.State switch
    {
        OverlayState.Error when view.Informational => ((char)0xE946).ToString(), // Info
        OverlayState.Error => ((char)0xE7BA).ToString(), // Warning
        OverlayState.Done => ((char)0xE73E).ToString(), // CheckMark
        OverlayState.Updating => ((char)0xE946).ToString(), // Info
        // Dictate's mode glyph is the mic, which made "Transcribing…" look like it was still recording.
        OverlayState.Working when view.Mode is Mode.Dictate or null => ((char)0xE895).ToString(), // Sync
        _ => view.Mode switch
        {
            Mode.Prompt => ((char)0xE8BD).ToString(), // Message
            Mode.Reply => ((char)0xE97A).ToString(), // Reply
            Mode.Raw => ((char)0xE8D2).ToString(), // Font
            _ => ((char)0xE720).ToString(), // Microphone
        },
    };

    /// <summary>Light, dark and high contrast. High contrast uses the system's own colours, with a visible border.</summary>
    private void ApplyTheme(Border root)
    {
        Windows.UI.Color bg, fg, edge;
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            bg = _uiSettings.UIElementColor(UIElementType.Window);
            fg = _uiSettings.UIElementColor(UIElementType.WindowText);
            edge = _uiSettings.UIElementColor(UIElementType.Highlight);
        }
        else
        {
            var systemBg = _uiSettings.GetColorValue(UIColorType.Background);
            var dark = systemBg.R + systemBg.G + systemBg.B < 384;
            bg = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 249, 249, 249);
            fg = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 26, 26, 26);
            edge = dark ? Windows.UI.Color.FromArgb(255, 64, 64, 64) : Windows.UI.Color.FromArgb(255, 220, 220, 220);
            root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        }
        root.Background = new SolidColorBrush(bg);
        root.BorderBrush = new SolidColorBrush(edge);
        var text = new SolidColorBrush(fg);
        foreach (var tb in new[] { _title, _elapsed, _detail, _previewText }) tb.Foreground = text;
        _icon.Foreground = text;
#if YOLIME_E2E
        var chose = (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "contrast"
            : root.RequestedTheme == ElementTheme.Dark ? "dark" : "light") + $" {bg.R},{bg.G},{bg.B}";
        if (root == _root) TestNotchTheme = chose;
        else TestPillTheme = chose;
#endif
    }

    /// <summary>Re-reads "hide from screen capture"; Settings calls it on the UI thread when the switch changes.</summary>
    public void ApplyCaptureSetting()
    {
        var hide = _settings().HideOverlayFromCapture;
        _notch.SetExcludeFromCapture(hide);
        _pill.SetExcludeFromCapture(hide);
    }

    /// <summary>False while a full-screen game, video or presentation would hide the topmost notch.</summary>
    private static bool CanShow()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return true;
        return state is not (3 or 4 or 5); // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
    }

    public void Dispose()
    {
        _disposed = true;
        _uiSettings.ColorValuesChanged -= _themeChanged;
        _timer.Stop();
        _notch.Dispose();
        _pill.Dispose();
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
