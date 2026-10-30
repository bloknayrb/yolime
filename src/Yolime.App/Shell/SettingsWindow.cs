using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Yolime.App.Audio;
using Yolime.App.Native;
using Yolime.Core;
using Yolime.Core.Hotkeys;
using Yolime.Core.Keys;
using Yolime.Core.Llm;
using Yolime.Core.Modes;
using Yolime.Core.Net;
using Yolime.Core.Prompts;
using Yolime.Core.Settings;
using Yolime.Core.Startup;
using Yolime.Core.Stt;

namespace Yolime.App.Shell;

/// <summary>
/// Settings: five pages built in code (like the overlay), every change saved through <see cref="SettingsHolder.Update"/>
/// and every rule taken from Core. A hand edit to settings.json re-renders the open page.
/// </summary>
/// <remarks>
/// yolime's own window, so dictation never pastes here (the planner copies instead), and key capture for the hold key
/// only works while it is in front.
/// </remarks>
internal sealed class SettingsWindow : Window
{
    public const string General = "general", KeysPage = "keys", Apps = "apps", OverlayHistory = "overlay", Advanced = "advanced";

    private readonly AppHost _host;
    private readonly NavigationView _nav = new();
    private readonly StackPanel _page = new() { Spacing = 14, Padding = new Thickness(28, 20, 28, 28), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly InfoBar _status = new() { IsClosable = true, Margin = new Thickness(28, 12, 28, 0) };
    private readonly KeyTester _tester;
    private readonly PromptLibrary _prompts;
    private string _current = General;
    private YolimeSettings? _ownChange;
    private CancellationTokenSource? _meter;
    private bool _dialogOpen;
    private Action<Yolime.Core.Updates.UpdateState>? _updateRow;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;

    public SettingsWindow(AppHost host, string page = General)
    {
        // Captured here, on the UI thread: Window.DispatcherQueue can't be read from the background threads that
        // raise history, settings and meter events (it throws RPC_E_WRONG_THREAD).
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _host = host;
        _tester = new KeyTester(HttpClients.Shared, host.Keys, () => host.Settings.Current);
        _prompts = new PromptLibrary(host.Paths);
        Title = "yolime settings";
        SystemBackdrop = new MicaBackdrop();

        foreach (var (tag, label, glyph) in new[]
        {
            (General, "General", "\ue713"),
            (KeysPage, "Keys", "\ue8d7"),
            (Apps, "Apps & words", "\ue71d"),
            (OverlayHistory, "Overlay & history", "\ue81c"),
            (Advanced, "Advanced", "\ue90f"),
        })
        {
            _nav.MenuItems.Add(new NavigationViewItem { Content = label, Tag = tag, Icon = new FontIcon { Glyph = glyph } });
        }
        _nav.IsSettingsVisible = false;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.IsPaneToggleButtonVisible = false;
        _nav.OpenPaneLength = 220;
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: string tag }) Show(tag);
        };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_status);
        var scroll = new ScrollViewer { Content = _page };
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);
        _nav.Content = body;
        Content = _nav;

        WindowSizing.SizeAndCentre(this, 980, 760);
        // Save results and errors are read out by Narrator as they appear.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);

        host.Settings.Changed += OnSettingsChanged;
        host.Keys.Changed += OnKeysChanged;
        Activated += (_, e) =>
        {
            // Leaving the window ends a key capture at once (the hook would otherwise wait for the next key) and the meter.
            if (e.WindowActivationState != WindowActivationState.Deactivated) return;
            _host.Hooks.CancelCapture();
            StopMeter();
        };
        Closed += (_, _) =>
        {
            host.Settings.Changed -= OnSettingsChanged;
            host.Keys.Changed -= OnKeysChanged;
            _host.Hooks.CancelCapture();
            StopMeter();
            DropUpdateRow();
        };
        Select(page);
    }

    /// <summary>Brings the window forward on a given page (the tray's "Finish setup\u2026" opens Keys).</summary>
    public void Select(string page)
    {
        foreach (var item in _nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((string)item.Tag == page) _nav.SelectedItem = item;
        }
        Activate();
    }

    private void OnSettingsChanged(YolimeSettings old, YolimeSettings now) => _ui.TryEnqueue(() =>
    {
        // Our own saves already show; only a hand edit (or the tray) needs the page redrawn.
        if (ReferenceEquals(now, _ownChange)) return;
        Show(_current);
    });

    private void OnKeysChanged(string slot) => _ui.TryEnqueue(() =>
    {
        if (_current == KeysPage) Show(KeysPage);
        else if (_current == General) Show(General);
    });

    private void Show(string page)
    {
        StopMeter();
        DropUpdateRow();
        _host.Hooks.CancelCapture();
        _current = page;
        _page.Children.Clear();
        switch (page)
        {
            case KeysPage: BuildKeys(); break;
            case Apps: BuildApps(); break;
            case OverlayHistory: BuildOverlayHistory(); break;
            case Advanced: BuildAdvanced(); break;
            default: BuildGeneral(); break;
        }
    }

    private YolimeSettings S => _host.Settings.Current;

    /// <summary>Saves a change, and says so inline when it couldn't be saved (memory is never ahead of the file).</summary>
    private bool Save(Func<YolimeSettings, YolimeSettings> change)
    {
        var result = _host.Settings.Update(change);
        _ownChange = _host.Settings.Current;
        switch (result)
        {
            case SaveResult.Saved:
                if (_status.Severity == InfoBarSeverity.Error) _status.IsOpen = false;
                return true;
            case SaveResult.ReadOnly:
                Say(InfoBarSeverity.Error, "Not saved", "settings.json comes from a newer yolime, so this version won't overwrite it.");
                return false;
            default:
                Say(InfoBarSeverity.Error, "Not saved", "yolime couldn't write settings.json. Check that the file isn't read-only or open elsewhere.");
                return false;
        }
    }

    private void Say(InfoBarSeverity severity, string title, string message)
    {
        // The InfoBar announces itself as it opens; only a change to one already open needs announcing.
        var wasOpen = _status.IsOpen;
        _status.Severity = severity;
        _status.Title = title;
        _status.Message = message;
        _status.IsOpen = true;
        if (wasOpen) Announce(_status);
    }

    /// <summary>Tells Narrator a live region changed; setting the text alone doesn't.</summary>
    private static void Announce(UIElement element) =>
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(element)
            ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);

    // ---------------------------------------------------------------- General

    private void BuildGeneral()
    {
        Heading("General");

        // Hold key
        var keyName = new TextBlock { Text = HoldKeyRules.Name(S.HoldKeyVk), VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var keyHelp = Help("Hold it, speak, and let go. Right Ctrl, Right Shift and Pause are allowed: they do nothing on their own.");
        var change = new Button { Content = "Change\u2026" };
        change.Click += async (_, _) =>
        {
            change.IsEnabled = false;
            keyHelp.Text = "Press the key you want to hold\u2026 (10 seconds)";
            var captured = await _host.Hooks.CaptureKeyAsync();
            change.IsEnabled = true;
            if (captured is not { } key)
            {
                keyHelp.Text = "No key pressed. Nothing changed.";
                return;
            }
            if (key.Vk is not { } code)
            {
                keyHelp.Text = HoldKeyRules.Reason(key.Verdict);
                return;
            }
            if (Save(s => s with { HoldKeyVk = code }))
            {
                keyName.Text = HoldKeyRules.Name(code);
                keyHelp.Text = "Saved. It works as soon as no dictation is in progress.";
            }
        };
        Row("Hold key", Horizontal(keyName, change), keyHelp);

        Toggle("Double-tap to keep recording", "Tap the hold key twice to record hands-free; tap once more to finish.",
            S.DoubleTapLatch, on => Save(s => s with { DoubleTapLatch = on }));

        // Paste-last chord
        var chord = new TextBox { Text = S.PasteLastHotkey, Width = 180 };
        var chordHelp = Help(_host.Tray.HotkeyProblem is { } problem ? "Not active: " + problem + "." : "Pastes your last result again, for up to 30 minutes.");
        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) =>
        {
            if (!HotkeyChord.TryParse(chord.Text, out _))
            {
                chordHelp.Text = "Use a modifier and a key, like Win+Alt+V or Ctrl+Shift+F9.";
                return;
            }
            var text = chord.Text.Trim();
            if (!Save(s => s with { PasteLastHotkey = text })) return;
            // Registered here as well as on the settings change, so Apply also retries a chord another app has freed.
            _host.Tray.RegisterPasteLast(text);
            chordHelp.Text = _host.Tray.HotkeyProblem is { } p ? "Not active: " + p + "." : "Saved and active.";
        };
        Row("Paste last result", Horizontal(chord, apply), chordHelp);

        // Microphone
        var mics = WasapiDevice.Microphones();
        var micBox = new ComboBox { MinWidth = 320 };
        micBox.Items.Add("Windows default");
        foreach (var (_, name) in mics) micBox.Items.Add(name);
        var selected = mics.ToList().FindIndex(m => m.Id == S.MicDeviceId);
        micBox.SelectedIndex = S.MicDeviceId is null ? 0 : selected >= 0 ? selected + 1 : -1;
        micBox.PlaceholderText = "Chosen microphone not connected";
        var micHelp = Help(S.MicDeviceId is not null && selected < 0
            ? "Your chosen microphone isn't connected; the Windows default is used until it is."
            : "The default follows Windows when you switch devices.");
        micBox.SelectionChanged += (_, _) =>
        {
            var id = micBox.SelectedIndex <= 0 ? null : mics[micBox.SelectedIndex - 1].Id;
            StopMeter();
            Save(s => s with { MicDeviceId = id });
        };
        var level = new ProgressBar { Width = 220, Maximum = 1, Value = 0, VerticalAlignment = VerticalAlignment.Center };
        var test = new ToggleButton { Content = "Test" };
        test.Checked += (_, _) => StartMeter(level, test);
        test.Unchecked += (_, _) => StopMeter();
        Row("Microphone", Horizontal(micBox, test, level), micHelp);
        Toggle("Keep the microphone open for a minute after each hold",
            "For a minute after each hold, the microphone stays open so the next hold starts without waiting for it. Meanwhile Windows shows that yolime is using the microphone, and a Bluetooth headset may stay in call mode, so music through it sounds worse (and briefly drops out when it closes). Everything heard between holds is thrown away as it arrives: nothing is kept, shown or sent. Pause hotkeys closes the microphone at once.",
            S.KeepMicReady, on => Save(s => s with { KeepMicReady = on }));

        Toggle("Sounds", "Short tones when recording starts and stops, and on errors.", S.Sounds, on => Save(s => s with { Sounds = on }));
        Toggle("Mode cues", "Beeps that say which mode a Shift tap switched to while recording (1 Dictate, 2 Prompt, 3 Reply, 4 Raw). Needs Sounds on.",
            S.ModeCycleCues, on => Save(s => s with { ModeCycleCues = on }));

        // Start with Windows
        var status = StartupRegistry.Status();
        var startHelp = Help(status switch
        {
            StartupStatus.OtherCopy => "The startup entry points to another copy of yolime.",
            StartupStatus.DisabledInTaskManager => "Turned off in Task Manager's Startup apps. Switching this on turns it back on.",
            _ => "Starts yolime in the tray when you sign in.",
        });
        var start = new ToggleSwitch { IsOn = status == StartupStatus.On, OnContent = "On", OffContent = "Off" };
        var revertingStart = false;
        start.Toggled += (_, _) =>
        {
            if (revertingStart) return;
            var ok = start.IsOn ? StartupRegistry.Enable() : StartupRegistry.Disable();
            if (ok)
            {
                startHelp.Text = start.IsOn ? "yolime starts when you sign in." : "yolime won't start when you sign in.";
                return;
            }
            Say(InfoBarSeverity.Error, "Couldn't change startup", "Windows refused the change to the startup entry.");
            revertingStart = true;
            start.IsOn = !start.IsOn;
            revertingStart = false;
        };
        var controls = new List<UIElement> { start };
        if (status == StartupStatus.OtherCopy)
        {
            var fix = new Button { Content = "Point it here" };
            fix.Click += (_, _) =>
            {
                if (StartupRegistry.Enable()) Show(General);
            };
            controls.Add(fix);
        }
        Row("Start with Windows", Horizontal([.. controls]), startHelp);

        BuildUpdateRow();

        if (!_host.SttReady)
        {
            var go = new HyperlinkButton { Content = "Add a transcription key" };
            go.Click += (_, _) => Select(KeysPage);
            _page.Children.Insert(1, new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Setup needed", Message = "yolime needs an OpenAI or Groq key to transcribe.", ActionButton = go });
        }
    }

    /// <summary>
    /// The bottom row: the version, "Check for updates automatically", Check now, and what the last check found (with
    /// Install when there's something to install). A copy that isn't installed says updates are off for it.
    /// </summary>
    private void BuildUpdateRow()
    {
        var updates = _host.Updates;
        var help = Help("");
        var auto = new ToggleSwitch { IsOn = S.CheckForUpdates, OnContent = "Check for updates automatically", OffContent = "Check for updates automatically", IsEnabled = updates.Installed };
        var reverting = false;
        auto.Toggled += (_, _) =>
        {
            if (reverting || Save(s => s with { CheckForUpdates = auto.IsOn })) return;
            reverting = true;
            auto.IsOn = !auto.IsOn;
            reverting = false;
        };
        var check = new Button { Content = "Check now", IsEnabled = updates.Installed };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            await updates.CheckAsync();
            check.IsEnabled = updates.Installed;
        };
        var install = new HyperlinkButton { Content = "Install", Visibility = Visibility.Collapsed };
        install.Click += (_, _) => _host.RequestUpdateWindow();
        void Render(Yolime.Core.Updates.UpdateState state)
        {
            help.Text = Yolime.Core.Updates.UpdateTexts.SettingsLine(updates.Installed, state, DateTimeOffset.Now, S.LastUpdateCheck, S.LastUpdateFailure);
            install.Visibility = state.Offered is null ? Visibility.Collapsed : Visibility.Visible;
        }
        Render(updates.State);
        _updateRow = Render;
        updates.StateChanged += Render;
        Row("yolime " + Yolime.Core.Updates.AppVersion.Display(updates.Product), Horizontal(auto, check, install), help);
        // The toggle's content is the same on and off, so Narrator needs its name; the button's content already is one.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(auto, "Check for updates automatically");
    }

    private void DropUpdateRow()
    {
        if (_updateRow is { } row) _host.Updates.StateChanged -= row;
        _updateRow = null;
    }

    private void StartMeter(ProgressBar level, ToggleButton button)
    {
        StopMeter();
        var cts = new CancellationTokenSource();
        _meter = cts;
        var id = S.MicDeviceId;
        _ = Task.Run(async () =>
        {
            try
            {
                // Directly, not through the app's device: a missing microphone here mustn't use up the once-per-session notice.
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
                _ui.TryEnqueue(() => Say(InfoBarSeverity.Warning, "Microphone", WelcomeWindow.MicTestProblem(ex)));
            }
            finally
            {
                _ui.TryEnqueue(() =>
                {
                    level.Value = 0;
                    button.IsChecked = false;
                });
            }
        });
    }

    private void StopMeter()
    {
        _meter?.Cancel();
        _meter = null;
    }

    // ---------------------------------------------------------------- Keys

    private void BuildKeys()
    {
        Heading("Keys");
        _page.Children.Add(Help("Keys are kept in Windows Credential Manager for your account, which protects them about as well as " +
            "environment variables do. Use project keys with a spending limit. A key saved here wins over an environment variable; " +
            "environment variables are read when yolime starts. ANTHROPIC_API_KEY is deliberately ignored, so yolime never spends " +
            "another tool's budget."));
        KeySlot(SttEndpoint.OpenAiKeyName, "OpenAI (transcription)", "Used when the provider is OpenAI, the default.");
        KeySlot(SttEndpoint.GroqKeyName, "Groq (transcription)", "Used when settings.json sets the provider to Groq.");
        KeySlot(ClaudeRewriter.KeyName, "Claude (Prompt, Reply, Dictate clean-up)", "Without it, Prompt and Reply copy the raw transcript.");
        if (S.SttProvider == SttProviderKind.Custom)
            KeySlot(SttEndpoint.CustomKeyName, "Custom transcription endpoint", "Sent only to the https endpoint in settings.json.");
    }

    private void KeySlot(string slot, string title, string help)
    {
        var status = _host.Keys.Status(slot);
        var state = new TextBlock
        {
            Text = status.Origin switch
            {
                KeyOrigin.Saved => status.LastFour is { } tail ? $"Saved (ends \u2026{tail})" : "Saved",
                KeyOrigin.Environment => status.LastFour is { } tail ? $"From the environment (ends \u2026{tail})" : "From the environment",
                _ => "Not set",
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new PasswordBox { Width = 300, PlaceholderText = status.Origin == KeyOrigin.None ? "Paste a key" : "Paste a new key to replace it" };
        var result = Help(help);
        var save = new Button { Content = "Save" };
        save.Click += (_, _) =>
        {
            var error = _host.Keys.Save(slot, box.Password);
            box.Password = "";
            result.Text = error switch
            {
                KeySaveError.None => "Saved.",
                KeySaveError.Empty => "Paste a key first.",
                KeySaveError.BadCharacters => "That doesn't look like a key: it has spaces or characters keys never contain.",
                KeySaveError.TooLong => "That's too long to be a key.",
                _ => "Windows Credential Manager refused to save it.",
            };
            Announce(result);
        };
        var testButton = new Button { Content = "Test" };
        testButton.Click += async (_, _) =>
        {
            testButton.IsEnabled = false;
            result.Text = "Testing\u2026";
            var candidate = box.Password;
            var outcome = await Guarded(() => _tester.TestAsync(slot, candidate, CancellationToken.None), KeyTestResult.Unreachable);
            testButton.IsEnabled = true;
            result.Text = outcome switch
            {
                KeyTestResult.Ok => "Works.",
                KeyTestResult.Rejected => "The provider rejected this key.",
                KeyTestResult.NotSet => "There's no key to test.",
                KeyTestResult.BadEndpoint => "Fix the endpoint in settings.json first (it must be https).",
                KeyTestResult.Malformed => "That doesn't look like a key: it has spaces or characters keys never contain.",
                _ => "Couldn't reach the provider. Check your connection and try again.",
            };
            Announce(result);
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, title + " key");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(save, "Save " + title + " key");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(testButton, "Test " + title + " key");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(result, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var controls = new List<UIElement> { box, save, testButton };
        if (status.Origin == KeyOrigin.Saved)
        {
            var remove = new Button { Content = "Remove" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove " + title + " key");
            remove.Click += async (_, _) =>
            {
                if (await Confirm("Remove this key?", "yolime forgets the saved key. An environment variable with the same name still works.", "Remove"))
                    _host.Keys.Remove(slot);
            };
            controls.Add(remove);
        }
        _page.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 10, 0, 0) });
        _page.Children.Add(state);
        _page.Children.Add(Horizontal([.. controls]));
        _page.Children.Add(result);
    }

    // ---------------------------------------------------------------- Apps & words

    private void BuildApps()
    {
        Heading("Apps & words");

        var modes = Enum.GetValues<Mode>();
        var starting = new ComboBox { ItemsSource = modes.Select(m => m.ToString()).ToList(), SelectedIndex = Array.IndexOf(modes, S.StartingMode) };
        starting.SelectionChanged += (_, _) => Save(s => s with { StartingMode = modes[starting.SelectedIndex] });
        Row("Starting mode", starting, Help("The mode a hold starts in, unless the app below or a terminal says otherwise."));

        _page.Children.Add(new TextBlock { Text = "Per-app starting modes", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        foreach (var (app, mode) in S.AppStartingModes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var remove = new Button { Content = "Remove" };
            var name = app;
            remove.Click += (_, _) =>
            {
                if (Save(s => SettingsEdits.WithAppMode(s, name, null))) Show(Apps);
            };
            _page.Children.Add(Horizontal(new TextBlock { Text = $"{app} \u2192 {mode}", Width = 280, VerticalAlignment = VerticalAlignment.Center }, remove));
        }
        var process = new TextBox { PlaceholderText = "Process name, e.g. slack", Width = 220 };
        var pick = new ComboBox { ItemsSource = modes.Select(m => m.ToString()).ToList(), SelectedIndex = 0 };
        var add = new Button { Content = "Add" };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(process.Text)) return;
            var text = process.Text;
            if (Save(s => SettingsEdits.WithAppMode(s, text, modes[pick.SelectedIndex]))) Show(Apps);
        };
        _page.Children.Add(Horizontal(process, pick, add));

        var words = new TextBox
        {
            Text = SettingsEdits.VocabularyText(S),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 180,
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
            PlaceholderText = "One per line: names, product terms, jargon",
        };
        var wordsHelp = Help("Sent with each recording so the transcriber spells these right.");
        var saveWords = new Button { Content = "Save words" };
        saveWords.Click += (_, _) =>
        {
            var list = SettingsEdits.ParseVocabulary(words.Text);
            if (Save(s => s with { Vocabulary = list })) wordsHelp.Text = $"Saved {list.Count} words.";
        };
        Row("Vocabulary", words, wordsHelp);
        _page.Children.Add(saveWords);
    }

    // ---------------------------------------------------------------- Overlay & history

    private void BuildOverlayHistory()
    {
        Heading("Overlay & history");

        var position = new ComboBox { ItemsSource = new List<string> { "Top centre", "Bottom centre" }, SelectedIndex = S.OverlayPosition == OverlayPosition.BottomCentre ? 1 : 0 };
        position.SelectionChanged += (_, _) => Save(s => s with { OverlayPosition = position.SelectedIndex == 1 ? OverlayPosition.BottomCentre : OverlayPosition.TopCentre });
        Row("Position", position, Help("Where the notch sits, on the monitor you're working on."));

        Toggle("Hide from screen capture", "Keeps the notch out of screenshots and screen sharing: it can show transcripts and reply context.",
            S.HideOverlayFromCapture, on => Save(s => s with { HideOverlayFromCapture = on }));

        _page.Children.Add(new TextBlock { Text = "Always preview before inserting", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var previews = new List<UIElement>();
        foreach (var mode in Enum.GetValues<Mode>())
        {
            var box = new CheckBox { Content = mode.ToString(), IsChecked = S.PreviewsAlways(mode) };
            var m = mode;
            box.Click += (_, _) => Save(s => SettingsEdits.WithPreview(s, m, box.IsChecked == true));
            previews.Add(box);
        }
        _page.Children.Add(Horizontal([.. previews]));
        _page.Children.Add(Help("Or keep Shift held as you let go of the hold key to preview that run; a quick Shift tap changes mode instead."));

        var days = new NumberBox { Minimum = 1, Maximum = 3650, Value = S.HistoryRetentionDays, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 160 };
        var daysHelp = Help("Older runs are deleted when yolime starts, and right away when you lower this.");
        var revertingDays = false;
        days.ValueChanged += async (_, e) =>
        {
            if (revertingDays || double.IsNaN(e.NewValue)) return;
            var value = (int)Math.Clamp(e.NewValue, 1, 3650);
            var old = S.HistoryRetentionDays;
            if (value == old) return;
            if (value < old && !await Confirm("Keep less history?", $"Runs older than {value} days are deleted now. This can't be undone.", "Delete older runs"))
            {
                revertingDays = true;
                days.Value = old;
                revertingDays = false;
                return;
            }
            if (!Save(s => s with { HistoryRetentionDays = value }))
            {
                revertingDays = true;
                days.Value = old;
                revertingDays = false;
                return;
            }
            if (value > old) return;
            var pruned = await Guarded(() => Task.FromResult(_host.PruneHistory(value)), false);
            daysHelp.Text = pruned ? $"Runs older than {value} days were deleted." : "Saved, but history couldn't be pruned now; it will be at the next start.";
        };
        Row("Keep history for (days)", days, daysHelp);

        Toggle("Pause history", "While on, yolime keeps nothing: no history, no recordings, nothing to retry or paste again.",
            S.HistoryPaused, on => Save(s => s with { HistoryPaused = on }));

        var clear = new Button { Content = "Clear history\u2026" };
        var clearHelp = Help("Deletes every run, transcript and kept recording.");
        clear.Click += async (_, _) =>
        {
            if (!await Confirm("Clear history?", "Every run, transcript and kept recording is deleted. This can't be undone.", "Clear")) return;
            clear.IsEnabled = false;
            clearHelp.Text = "Clearing\u2026 (waits for a dictation in progress)";
            var ok = await Guarded(_host.ClearHistoryAsync, false);
            clear.IsEnabled = true;
            clearHelp.Text = ok ? "History cleared." : "History couldn't be cleared: its database isn't writable.";
        };
        Row("Clear", clear, clearHelp);
    }

    // ---------------------------------------------------------------- Advanced

    private void BuildAdvanced()
    {
        Heading("Advanced");

        var models = new List<string> { ModelIds.Sonnet, ModelIds.Haiku };
        // Named as the help text names them; the ids stay in settings.json.
        var model = new ComboBox { ItemsSource = new List<string> { "Sonnet", "Haiku" }, SelectedIndex = Math.Max(0, models.IndexOf(S.PromptModel)) };
        model.SelectionChanged += (_, _) => Save(s => s with { PromptModel = models[model.SelectedIndex] });
        Row("Prompt model", model, Help("Sonnet writes better prompts; Haiku is faster and cheaper."));

        Toggle("Send audio while you speak (OpenAI)",
            "Key-up then waits only for transcription: about 0.07 s quicker for 10 s of speech, 0.2 s for 30 s. The cost: if you then cancel a hold (Esc, another key, switching apps), what you'd said has already reached OpenAI. The upload is stopped before it's complete, so nothing is transcribed, but what was sent can't be recalled.",
            S.StreamingUpload, on => Save(s => s with { StreamingUpload = on }));

        _page.Children.Add(new TextBlock { Text = "Prompts", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        foreach (var (id, label) in new[] { ("dictate", "Dictate"), ("prompt", "Prompt"), ("reply", "Reply") })
        {
            PromptFile file;
            try { file = _prompts.Get(id); }
            catch (KeyNotFoundException) { continue; }
            var state = file.IsUserOverride
                ? _prompts.NewerDefaultExists(id) ? "Your copy (a newer default is available)" : "Your copy"
                : $"Default (v{file.Version})";
            var edit = new Button { Content = "Edit" };
            edit.Click += (_, _) =>
            {
                try
                {
                    Open(_prompts.CreateOverride(id));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Say(InfoBarSeverity.Error, "Couldn't create your copy", "yolime couldn't write to its prompts folder.");
                }
            };
            var controls = new List<UIElement> { new TextBlock { Text = $"{label}: {state}", Width = 340, VerticalAlignment = VerticalAlignment.Center }, edit };
            if (file.IsUserOverride)
            {
                var resetPrompt = new Button { Content = "Reset to default" };
                resetPrompt.Click += async (_, _) =>
                {
                    if (!await Confirm($"Reset the {label} prompt?", "Your copy is kept next to it as a backup, and the default applies again.", "Reset")) return;
                    try
                    {
                        _prompts.ResetToDefault(id);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Say(InfoBarSeverity.Error, "Couldn't reset the prompt", "The prompt file is open elsewhere or read-only.");
                    }
                    Show(Advanced);
                };
                controls.Add(resetPrompt);
            }
            _page.Children.Add(Horizontal([.. controls]));
        }
        _page.Children.Add(Help("Edits apply from the next dictation."));

        var json = new Button { Content = "Open settings.json" };
        json.Click += (_, _) =>
        {
            if (!File.Exists(_host.Paths.SettingsFile)) Save(s => s);
            Open(_host.Paths.SettingsFile);
        };
        Row("Settings file", json, Help("The transcription provider, model and endpoint live only here. Changes apply when you save the file."));

        var reset = new Button { Content = "Reset yolime\u2026" };
        reset.Click += async (_, _) =>
        {
            if (!await Confirm("Reset yolime?", "This removes your saved keys, history, recordings, prompts, settings and the startup entry, then closes yolime. " +
                "Environment variables aren't touched.", "Reset and close")) return;
            _host.RequestReset();
        };
        Row("Reset", reset, Help("Back to how it was before the first run."));
    }

    // ---------------------------------------------------------------- helpers

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
        _page.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 0, 0, 4) });

    private static TextBlock Help(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
        MaxWidth = 640,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private void Row(string label, UIElement control, TextBlock help)
    {
        // Narrator reads the label with the control; in a row of several, the first control carries it.
        var target = control is Panel p && p.Children.Count > 0 ? p.Children[0] : control;
        if (string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(target)))
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(target, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(help, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        // A fixed-width control would otherwise be centred in the page's column.
        if (control is FrameworkElement { Width: > 0 } fixedWidth) fixedWidth.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(control);
        panel.Children.Add(help);
        _page.Children.Add(panel);
    }

    private void Toggle(string label, string help, bool value, Func<bool, bool> save)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "On", OffContent = "Off" };
        var reverting = false;
        toggle.Toggled += (_, _) =>
        {
            // A refused save puts the switch back, so it never shows a state the file doesn't have.
            if (reverting || save(toggle.IsOn)) return;
            reverting = true;
            toggle.IsOn = !toggle.IsOn;
            reverting = false;
        };
        Row(label, toggle, Help(help));
    }

    private static StackPanel Horizontal(params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in items) panel.Children.Add(item);
        return panel;
    }

    /// <summary>
    /// Asks before something destructive. WinUI throws if a second dialog opens while one is up (a double click), and
    /// a throw here would end the app, so a second request is simply refused.
    /// </summary>
    private async Task<bool> Confirm(string title, string message, string action)
    {
        if (_dialogOpen) return false;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            _host.Log.Write(Yolime.Core.Logging.LogEvent.UnhandledException, Yolime.Core.Logging.LogField.Error(ex));
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>Opens a file in its default app; with none set for it (often .md), says so instead of throwing.</summary>
    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Say(InfoBarSeverity.Warning, "Couldn't open the file", "Windows has no app set for it. It's at " + path);
        }
    }
}
