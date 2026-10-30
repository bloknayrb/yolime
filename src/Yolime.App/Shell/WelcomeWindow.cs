using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yolime.App.Audio;
using Yolime.App.Native;
using Yolime.Core.Hotkeys;
using Yolime.Core.Keys;
using Yolime.Core.Llm;
using Yolime.Core.Net;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.App.Shell;

/// <summary>
/// The first-run wizard: keys, microphone, "hold your key", a practice box, done. Every step can be skipped, and
/// closing the window counts as Skip; either way it never opens on its own again (<see cref="FirstRun.ShowWizard"/>).
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private const int Steps = 5;

    private readonly AppHost _host;
    private readonly KeyTester _tester;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _stepLabel = new() { Opacity = 0.7 };
    private readonly Button _back = new() { Content = "Back" };
    private readonly HyperlinkButton _skip = new() { Content = "Skip setup" };
    // Shown under the mic test's message only when Windows denied the mic (not when there is none).
    private readonly HyperlinkButton _micPrivacy = new() { Content = "Open privacy settings", Visibility = Visibility.Collapsed };
    private readonly Button _next = new() { Content = "Next", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly nint _hwnd;
    private int _step;
    private bool _finished;
    private CancellationTokenSource? _meter;
    private Action<bool>? _holdHandler;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;

    public WelcomeWindow(AppHost host)
    {
        // Captured here, on the UI thread: Window.DispatcherQueue can't be read from the background threads that
        // raise history, settings and meter events (it throws RPC_E_WRONG_THREAD).
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _host = host;
        _tester = new KeyTester(HttpClients.Shared, host.Keys, () => host.Settings.Current);
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "Welcome to yolime";
        SystemBackdrop = new MicaBackdrop();

        _skip.Click += (_, _) => Finish();
        _micPrivacy.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) =>
        {
            if (_step == Steps - 1) Finish();
            else Go(_step + 1);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_skip);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);

        var root = new Grid { Padding = new Thickness(32, 24, 32, 24), RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_stepLabel);
        var scroll = new ScrollViewer { Content = _body };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;

        WindowSizing.SizeAndCentre(this, 720, 620);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) StopMeter();
            // Back from checking the privacy setting: the meter starts again rather than sitting frozen.
            else if (_step == 1 && _meter is null) Go(1);
        };
        Closed += (_, _) =>
        {
            LeaveStep();
            // Closing the window is Skip: the wizard doesn't come back on the next start.
            if (!_finished) MarkDone();
        };
        Go(0);
        Activate();
    }

    /// <summary>The app is exiting or resetting: close without recording anything.</summary>
    public void CloseForShutdown()
    {
        _finished = true;
        LeaveStep();
        Close();
    }

    private void Finish()
    {
        _finished = true;
        LeaveStep();
        MarkDone();
        Close();
    }

    private void MarkDone() => _host.Settings.Update(s => s with { FirstRunDone = true });

    /// <summary>Undoes whatever the current step switched on: run suppression, the practice exception, the meter.</summary>
    private void LeaveStep()
    {
        StopMeter();
        _host.Hooks.SuppressRuns = false;
        if (_holdHandler is not null) _host.Hooks.HoldKeySeen -= _holdHandler;
        _holdHandler = null;
        if (TargetReader.PracticeWindow == _hwnd) TargetReader.PracticeWindow = 0;
    }

    private void Go(int step)
    {
        LeaveStep();
        _step = Math.Clamp(step, 0, Steps - 1);
        _stepLabel.Text = $"Step {_step + 1} of {Steps}";
        _back.IsEnabled = _step > 0;
        _next.Content = _step == Steps - 1 ? "Finish" : "Next";
        // On the last step Skip would do exactly what Finish does.
        _skip.Visibility = _step == Steps - 1 ? Visibility.Collapsed : Visibility.Visible;
        _body.Children.Clear();
        switch (_step)
        {
            case 0: KeysStep(); break;
            case 1: MicStep(); break;
            case 2: HoldStep(); break;
            case 3: PracticeStep(); break;
            default: DoneStep(); break;
        }
    }

    private void KeysStep()
    {
        Heading("Hold a key, speak, and your words appear wherever you're typing.");
        Text("yolime needs a transcription key. An OpenAI key is the default; Claude is optional and powers Prompt, Reply " +
             "and Dictate clean-up. Keys are stored in Windows Credential Manager for your account. Use project keys with " +
             "a spending limit.");
        KeyRow(SttEndpoint.OpenAiKeyName, "OpenAI (transcription)");
        KeyRow(ClaudeRewriter.KeyName, "Claude (Prompt, Reply, Dictate clean-up), optional");
    }

    private void KeyRow(string slot, string label)
    {
        var status = _host.Keys.Status(slot);
        var state = new TextBlock
        {
            Text = status.Origin switch
            {
                KeyOrigin.Saved => "Saved.",
                KeyOrigin.Environment => "Found in your environment variables.",
                _ => "Not set yet.",
            },
            Opacity = 0.75,
        };
        var box = new PasswordBox { Width = 320, PlaceholderText = "Paste a key" };
        var save = new Button { Content = "Save and test" };
        // Enter in the box saves and tests, unless a test is already running.
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || !save.IsEnabled) return;
            e.Handled = true;
            _ = SaveAndTestAsync();
        };
        save.Click += async (_, _) => await SaveAndTestAsync();
        async Task SaveAndTestAsync()
        {
            var key = box.Password;
            if (key.Length > 0)
            {
                var error = _host.Keys.Save(slot, key);
                box.Password = "";
                if (error != KeySaveError.None)
                {
                    state.Text = error == KeySaveError.StoreFailed ? "Windows refused to save it." : "That doesn't look like a key.";
                    return;
                }
            }
            save.IsEnabled = false;
            state.Text = "Testing\u2026";
            var result = await Guarded(() => _tester.TestAsync(slot, null, CancellationToken.None), KeyTestResult.Unreachable);
            save.IsEnabled = true;
            state.Text = result switch
            {
                KeyTestResult.Ok => "Works.",
                KeyTestResult.Rejected => "The provider rejected this key. Check it.",
                KeyTestResult.Malformed => "That doesn't look like a key.",
                KeyTestResult.NotSet => "Paste a key first.",
                _ => "Saved, but the provider couldn't be reached to check it.",
            };
        }
        _body.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(save);
        _body.Children.Add(row);
        _body.Children.Add(state);
    }

    private void MicStep()
    {
        Heading("Your microphone");
        var mics = WasapiDevice.Microphones();
        var box = new ComboBox { MinWidth = 340 };
        box.Items.Add("Windows default");
        foreach (var (_, name) in mics) box.Items.Add(name);
        var index = mics.ToList().FindIndex(m => m.Id == _host.Settings.Current.MicDeviceId);
        box.SelectedIndex = index >= 0 ? index + 1 : 0;
        var level = new ProgressBar { Width = 340, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Left };
        var note = new TextBlock { Text = "Say something: the bar should move.", Opacity = 0.75 };
        box.SelectionChanged += (_, _) =>
        {
            var id = box.SelectedIndex <= 0 ? null : mics[box.SelectedIndex - 1].Id;
            _host.Settings.Update(s => s with { MicDeviceId = id });
            StartMeter(level, note);
        };
        _body.Children.Add(box);
        _body.Children.Add(level);
        _body.Children.Add(note);
        _micPrivacy.Visibility = Visibility.Collapsed;
        _body.Children.Add(_micPrivacy);
        StartMeter(level, note);
    }

    private void HoldStep()
    {
        var current = _host.Settings.Current.HoldKeyVk;
        Heading($"Hold {HoldKeyRules.Name(current)}");
        Text("Press and hold it now. Nothing is recorded on this page.");
        var light = new Border
        {
            Width = 160,
            Height = 48,
            CornerRadius = new CornerRadius(24),
            HorizontalAlignment = HorizontalAlignment.Left,
            // Theme brushes: the fixed grey and green read poorly (white on light green was about 2:1) and ignored dark mode.
            Background = Brush("ControlFillColorSecondaryBrush"),
            BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "Not held", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorPrimaryBrush") },
        };
        _body.Children.Add(light);
        _holdHandler = down => _ui.TryEnqueue(() =>
        {
            light.Background = Brush(down ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush");
            ((TextBlock)light.Child).Foreground = Brush(down ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
            ((TextBlock)light.Child).Text = down ? "Held: it works" : "Not held";
        });
        _host.Hooks.HoldKeySeen += _holdHandler;
        _host.Hooks.SuppressRuns = true;

        Text("If that key doesn't suit your keyboard, choose another:");
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var vk in new[] { HoldKeyRules.RightCtrl, HoldKeyRules.RightShift, HoldKeyRules.Pause })
        {
            var b = new RadioButton { Content = HoldKeyRules.Name(vk), IsChecked = vk == current, GroupName = "holdkey" };
            var code = vk;
            b.Checked += (_, _) =>
            {
                if (code == _host.Settings.Current.HoldKeyVk) return;
                _host.Settings.Update(s => s with { HoldKeyVk = code });
                // Updated in place, not by rebuilding the step: a rebuild dropped keyboard focus after one arrow press.
                if (_body.Children.Count > 0 && _body.Children[0] is TextBlock heading) heading.Text = $"Hold {HoldKeyRules.Name(code)}";
            };
            choices.Children.Add(b);
        }
        _body.Children.Add(choices);
    }

    private void PracticeStep()
    {
        var key = HoldKeyRules.Name(_host.Settings.Current.HoldKeyVk);
        Heading("Try it");
        Text($"Click in the box, hold {key}, say a sentence, and let go. Tap Shift while holding to change mode, or keep it held as you let go to preview first.");
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160, PlaceholderText = "Your words land here" };
        _body.Children.Add(box);
        // The one yolime window dictation may paste into.
        TargetReader.PracticeWindow = _hwnd;
        if (!_host.SttReady) Text("There's no transcription key yet, so this won't work until you add one (step 1).");
        box.Focus(FocusState.Programmatic);
    }

    private void DoneStep()
    {
        Heading("yolime is running");
        Text($"It lives in the notification area. To keep its icon in view, open the ^ overflow by the clock and drag yolime onto the taskbar.");
        var chord = _host.Tray.HotkeyProblem is null
            ? $"{_host.Settings.Current.PasteLastHotkey} pastes your last result again. "
            : "The paste-last shortcut isn't active (another app holds it); choose another in Settings. ";
        Text($"Hold {HoldKeyRules.Name(_host.Settings.Current.HoldKeyVk)} to dictate anywhere. " + chord + "Right-click the tray icon for Settings.");
    }

    private void StartMeter(ProgressBar level, TextBlock note)
    {
        StopMeter();
        var cts = new CancellationTokenSource();
        _meter = cts;
        var id = _host.Settings.Current.MicDeviceId;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var live = new WasapiLive(_host.Log, id);
                var last = 0L;
                await foreach (var chunk in live.ReadAsync(cts.Token))
                {
                    var now = Environment.TickCount64;
                    if (now - last < 80) continue;
                    last = now;
                    var value = Math.Min(1, Math.Sqrt(chunk.Rms) * 4);
                    _ui.TryEnqueue(() => level.Value = value);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is Yolime.Core.Audio.AudioCaptureException or System.Runtime.InteropServices.COMException)
            {
                _ui.TryEnqueue(() =>
                {
                    note.Text = MicTestProblem(ex);
                    var noMic = ex is Yolime.Core.Audio.AudioCaptureException { Error: Yolime.Core.Audio.CaptureError.NoDevice };
                    _micPrivacy.Visibility = noMic ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        });
    }

    /// <summary>Why the mic test couldn't start: no microphone isn't a privacy setting.</summary>
    internal static string MicTestProblem(Exception ex) => ex is Yolime.Core.Audio.AudioCaptureException { Error: Yolime.Core.Audio.CaptureError.NoDevice }
        ? "No microphone found. Connect one, then try again."
        : "yolime couldn't open the microphone. Check Windows' microphone privacy setting.";

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void StopMeter()
    {
        _meter?.Cancel();
        _meter = null;
    }

    /// <summary>
    /// Runs background work for a button. An exception escaping an async click handler ends the whole app (a stowed
    /// exception on the dispatcher), so anything unexpected is logged and turned into <paramref name="failed"/>.
    /// </summary>
    private async Task<T> Guarded<T>(Func<Task<T>> work, T failed)
    {
        try
        {
            return await Task.Run(work);
        }
        catch (Exception ex)
        {
            _host.Log.Write(Yolime.Core.Logging.LogEvent.UnhandledException, Yolime.Core.Logging.LogField.Error(ex));
            return failed;
        }
    }

    private void Heading(string text) =>
        _body.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap });

    private void Text(string text) =>
        _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
}
