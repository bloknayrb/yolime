using Yolime.App.Audio;
using Yolime.App.Native;
using Yolime.App.Shell;
using Yolime.Core;
using Yolime.Core.Audio;
using Yolime.Core.Capture;
using Yolime.Core.Clipboard;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Hotkeys;
using Yolime.Core.Keys;
using Yolime.Core.Llm;
using Yolime.Core.Logging;
using Yolime.Core.Modes;
using Yolime.Core.Net;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;
using Yolime.Core.Prompts;
using Yolime.Core.Settings;
using Yolime.Core.Startup;
using Yolime.Core.Stt;
using Yolime.Core.Updates;

namespace Yolime.App;

/// <summary>What the E2E build swaps in; null everywhere in a normal run.</summary>
/// <param name="UpdateHttp">E2E: the update check and download go to a file-based fake instead of GitHub.</param>
internal sealed record HostOverrides(AppPaths? Paths = null, ICaptureDevice? Device = null, ITranscriptionSessionFactory? Stt = null, IRewriter? Rewriter = null, Func<IRawCapture>? RawMic = null,
    HttpClient? UpdateHttp = null, string? UpdateOwner = null, string? UpdateRepo = null);

/// <summary>Builds and owns everything the running app needs, on the UI thread.</summary>
internal sealed class AppHost : IDisposable
{
    private readonly FileLog _log;
    private readonly HistoryStore _history;
    private readonly TargetReader _targets;
    private readonly OutputExecutor _executor;
    private readonly HookThread _hooks;
    private readonly TrayIcon _tray;
    private readonly ClipboardOwnerThread _clipboardOwner;
    private readonly Shell.OverlayController _overlay;
    private readonly ContextReader _context;
    private readonly ITranscriptionSessionFactory _stt;
    private readonly IRewriter _rewriter;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;
    private volatile TargetSnapshot? _foreground;
    private volatile bool _recording;
    private volatile bool _micDenied;
    private volatile bool _micOpen;
    private ReadyMicDevice? _readyMic;
    private MicChangeWatcher? _micWatcher;
    private int _elevatedWarned;
    private readonly Lock _lastAppGate = new();
    private AppWindowId? _lastApp;
    private Timer? _pruneTimer;

    public AppHost(HostOverrides? overrides = null)
    {
        var paths = overrides?.Paths ?? AppPaths.Default();
        Paths = paths;
        var time = TimeProvider.System;
        _log = new FileLog(paths, time);
        Program.InstallCrashLogging(_log);
        _log.Write(LogEvent.AppStarted);

        var store = new SettingsStore(paths, _log);
        var loaded = store.Load();
        // Every reader takes Settings.Current when it needs it, so a change in Settings applies without a restart.
        Settings = new SettingsHolder(store, loaded.Settings, _log);
        var initial = loaded.Settings;
        var pending = new PendingAudioStore(paths, _log, time);
        _history = new HistoryStore(paths, _log, time, pending);
        // Before any run: marks crashed runs Interrupted, so it must never race a live one.
        Housekeeping.Run(_history, pending, initial);

        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _tray = new TrayIcon(() => Settings.Current.Sounds);
        _targets = new TargetReader();
        _ = Task.Run(_targets.Warm);
        // One source for every key: saved in Settings (Credential Manager), else the environment.
        Keys = new KeySource(new WindowsCredentialStore());
        _stt = overrides?.Stt ?? new BatchTranscriptionSessionFactory(() => Settings.Current, Keys, HttpClients.Shared, _log, time);
        _rewriter = overrides?.Rewriter ?? new ClaudeRewriter(HttpClients.Shared, Keys, new PromptBuilder(new PromptLibrary(paths)), () => Settings.Current, _log);

        // Every clipboard change is recorded by sequence number; the executor marks the ones yolime caused.
        var freshness = new ClipboardFreshness(time);
        _tray.ClipboardChanged += () => freshness.OnChanged(User32.GetClipboardSequenceNumber());
        if (!_tray.ClipboardListening) _log.Write(LogEvent.ClipboardListenerFailed);
        // The owner of yolime's clipboard content is a thread of its own, never the UI thread: another app's paste of
        // an offered (delayed-render) copy waits on the owner's messages.
        _clipboardOwner = new ClipboardOwnerThread(_log);
        var clipboard = new ClipboardGuard(_clipboardOwner.Hwnd, _log);
        _clipboardOwner.Attach(clipboard);
        _context = new ContextReader(clipboard, freshness, _log);

        var sounds = new Shell.ToneSounds(() => Settings.Current);
        // The notch: run events and notices go to it, and to a balloon only while a full-screen app would hide it.
        _overlay = new Shell.OverlayController(_ui, () => Settings.Current, _tray);
        HookThread? hooks = null;
        var preview = new Shell.PreviewPrompt(_overlay, () => hooks!);
        var focusWaiter = new FocusWaiter(() => hooks!, _targets);

        CaptureCoordinator? coordinator = null;
        _executor = new OutputExecutor(clipboard, new Injector(), _targets, _overlay,
            runId => coordinator?.StartSnapshot(runId), time, _log, freshness, preview, focusWaiter);
        _executor.PendingPaste += seconds => _overlay.Post(seconds > 0
            ? new Yolime.Core.Overlay.PendingPasteStarted(seconds, Shell.OverlayController.NowMs)
            : new Yolime.Core.Overlay.PendingPasteEnded());
        Pipeline = new DictationPipeline(_stt, _rewriter, _executor, _history, pending, _log, new JsonlTimingLog(paths, time), () => Settings.Current, time);
        LastResult = new LastResult(time, () => Settings.Current.HistoryPaused);

        var warmer = new ConnectionWarmer(HttpClients.Shared, time, _log,
            b => b == ConnectionWarmer.ClaudeHost ? _rewriter.CheckReady() == RewriterReadiness.Ready : _stt.CheckReady() == SttReadiness.Ready);
        HostPinningHandler.RequestCompleted += warmer.MarkUsed;
        var machine = new HoldStateMachine(new HoldOptions(DoubleTapLatch: initial.DoubleTapLatch, ModeCycling: true));
        var mic = new WasapiDevice(_log, () => Settings.Current.MicDeviceId, time);
        // Once per session: an unplugged favourite shouldn't nag on every hold.
        var micFallbackShown = 0;
        void FellBack()
        {
            if (Interlocked.Exchange(ref micFallbackShown, 1) == 0) _overlay.Show(new Notice(NoticeKind.MicFallback));
        }
        mic.FellBack += FellBack;
        // "Keep the microphone open for a minute after each hold": off, every hold goes to the cold device as before.
        var ready = new ReadyMicDevice(overrides?.RawMic ?? (() =>
        {
            var raw = new WasapiRawCapture(Settings.Current.MicDeviceId);
            if (raw.FellBack) FellBack();
            return raw;
        }), () => Settings.Current.KeepMicReady, overrides?.Device ?? mic, time, _log);
        ready.OpenChanged += open =>
        {
            _micOpen = open;
            _ui.TryEnqueue(RefreshTray);
        };
        _readyMic = ready;
        // A device change (a headset connecting, a new default) closes it, so the next hold opens the right one.
        if (overrides?.RawMic is null) _micWatcher = new MicChangeWatcher(ready.Suspend);
        coordinator = new CaptureCoordinator(Pipeline, ready, _targets, _stt, _history, _tray, sounds,
            act => hooks!.OnMachine(act), time, _log, _context, () => Settings.Current, overlay: _overlay.Post, displayName: DisplayNames.For,
            lastResult: LastResult, output: _executor,
            holdStarted: () => _ = Task.Run(() => warmer.WarmAsync(Settings.Current)));
        Coordinator = coordinator;
        hooks = new HookThread(initial.HoldKeyVk, machine, () => StartingModes.For(ForegroundNow(), Settings.Current), coordinator.Handle, _log);
        _hooks = hooks;
        hooks.IsOnCard = _overlay.IsOnCard;
        hooks.UserKeyPressed += () => _overlay.Post(new Yolime.Core.Overlay.UserTyped());
        // Key presses are only reported while a "Use raw" offer is up: each one costs a post to the UI thread.
        _overlay.ViewChanged += view => hooks.WatchKeys = view.OfferUseRaw;
        coordinator.HotkeyTapped += _overlay.HotkeyTapped;
        _overlay.Retry += id => _ = Task.Run(() => coordinator.RetryAsync(id, CancellationToken.None));
        _overlay.UseRaw += id => _ = Task.Run(async () =>
        {
            if (_history.Get(id)?.RawText is { } raw) await _executor.CopyForUserAsync(raw, NoticeKind.RawCopied, CancellationToken.None);
        });
        _overlay.OpenMicSettings += () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });

        coordinator.RecordingChanged += recording =>
        {
            _recording = recording;
            RefreshTray();
            if (recording) WarnIfElevatedOnce();
        };
        coordinator.NoticeShown += notice =>
        {
            if (notice.Kind != NoticeKind.MicDenied) return;
            _micDenied = true;
            RefreshTray();
        };
        coordinator.RunFinished += result =>
        {
            if (!_micDenied || result.Outcome is RunOutcome.CaptureFailed or RunOutcome.Busy) return;
            _micDenied = false; // the mic worked this time
            RefreshTray();
        };
        // Off the UI thread, like every other run: its continuations must not depend on a thread that exit blocks.
        _tray.RetryRequested += () => _ = Task.Run(() => coordinator.RetryLastAsync(CancellationToken.None));
        _tray.PasteLastRequested += () => _ = Task.Run(() => coordinator.PasteLastAsync(CancellationToken.None));
        _tray.PauseChanged += paused =>
        {
            _hooks.Paused = paused;
            // Pause hotkeys is the instant off switch for a kept-ready mic too.
            if (paused) _readyMic?.Suspend();
            else Updates?.Resumed();
            RefreshTray();
        };
        _tray.HistoryPauseChanged += paused =>
        {
            // The check mark shows what settings.json says, so a save that failed can't claim history is paused.
            if (Settings.Update(s => s with { HistoryPaused = paused }) != SaveResult.Saved)
                _tray.Show(new Notice(NoticeKind.SettingsNotSaved));
            _tray.SetHistoryPaused(Settings.Current.HistoryPaused);
        };
        _tray.SetHistoryPaused(initial.HistoryPaused);
        _tray.RegisterPasteLast(initial.PasteLastHotkey);
        // The last window the user worked in that isn't yolime's or the shell's (taskbar, tray overflow).
        hooks.ForegroundSwitched += hwnd =>
        {
            // Not yolime's own windows, and only real ones (shown, not cloaked, not the taskbar): Start, Search and
            // Quick Settings stay alive but cloaked once closed, and a paste sent there would vanish.
            var window = HookThread.Sample(hwnd);
            if (window.Pid == Environment.ProcessId || !window.IsReal) return;
            // With its process's identity, so History's Re-insert can tell a recycled handle from the real window.
            var id = AppWindowId.Of(hwnd);
            lock (_lastAppGate) _lastApp = id;
        };
        _tray.LastAppWindow = () => LastAppWindow?.Hwnd ?? 0;
        _tray.HistoryRequested += () => HistoryRequested?.Invoke();
        coordinator.HistoryChanged += () => HistoryChanged?.Invoke(HistoryChange.Updated);
        // Retention for an app that stays up for weeks: prune every six hours, not only at startup.
        _pruneTimer = new Timer(_ =>
        {
            // A timer callback that throws ends the process, and this one can race shutdown.
            try { PruneHistory(Settings.Current.HistoryRetentionDays); }
            catch (Exception ex) { _log.Write(LogEvent.UnhandledException, LogField.Error(ex)); }
        }, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
        _tray.SettingsRequested += () => SettingsRequested?.Invoke();
        _tray.FinishSetupRequested += () =>
        {
            // "Setup needed" has two causes; the microphone one is fixed in Windows' privacy settings, not in Keys.
            if (SttReady && _micDenied)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
            else FinishSetupRequested?.Invoke();
        };
        _tray.SessionEnded += _hooks.SessionEnded;
        // Locked, disconnected or going to sleep: never keep a mic open on a room nobody is in.
        _tray.SessionEnded += () => _readyMic?.Suspend();
        _tray.SessionResumed += _hooks.ReArm;
        _tray.SessionResumed += () => _ui.TryEnqueue(() => Updates?.Resumed());
        _tray.EndingSession += ReleaseClipboard;
        _tray.ExitRequested += () => ExitRequested?.Invoke();

        // Updates: the daily check, the offer, and Install. Only an installed, non-dev copy ever checks (Updater.Installed).
        var owner = overrides?.UpdateOwner ?? "bloknayrb";
        var repo = overrides?.UpdateRepo ?? "yolime";
        var updates = new Updater(_ui, new UpdateClient(overrides?.UpdateHttp ?? HttpClients.Shared, _log, owner, repo), Settings, _log,
            paths.UpdatesDir, _targets.SelfElevated, () => coordinator.InProgress, () => _tray.HotkeysPaused, owner, repo);
        Updates = updates;
        updates.StateChanged += state =>
        {
            _hooks.RefuseHolds = state.RefuseHolds;
            _tray.SetOfferedUpdate(state.Offered);
        };
        updates.Balloon += _tray.Show;
        updates.Overlay += _overlay.Post;
        hooks.HoldRefused += updates.HoldRefused;
        _overlay.OpenUpdateLog += updates.OpenLog;
        _tray.InstallUpdateRequested += () => UpdateWindowRequested?.Invoke();
        _tray.ReleasePageRequested += updates.OpenReleasePage;
        _tray.UpdateLogRequested += updates.OpenLog;

        Settings.Changed += OnSettingsChanged;
        Keys.Changed += _ => RefreshTray();
        Settings.StartWatching();

        RefreshTray();
        // An update's result takes the Started balloon's place (once: reading it clears it). A pending HoldKeyReset
        // follows a few seconds later, since a second balloon at once would replace the first unread.
        var holdKeyReset = loaded.InvalidFields.Contains("holdKeyVk");
        var startup = updates.StartupNotice();
        if (startup is not null)
        {
            _tray.Show(startup);
            if (holdKeyReset) _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => _ui.TryEnqueue(() => _tray.Show(new Notice(NoticeKind.HoldKeyReset))), TaskScheduler.Default);
        }
        else
        {
            _tray.Show(holdKeyReset ? new Notice(NoticeKind.HoldKeyReset) : new Notice(NoticeKind.Started, HoldKey: initial.HoldKeyVk));
        }
        updates.Start();
#if YOLIME_E2E
        E2E.E2EMode.Record(coordinator, _overlay, updates, startup);
#endif
    }

    public AppPaths Paths { get; }
    public ILog Log => _log;
    public SettingsHolder Settings { get; }
    public KeySource Keys { get; }
    public LastResult LastResult { get; }
    public DictationPipeline Pipeline { get; }
    public CaptureCoordinator Coordinator { get; }
    public TrayIcon Tray => _tray;
    public HookThread Hooks => _hooks;
    public TargetReader Targets => _targets;
    public Updater Updates { get; }

    /// <summary>The update window: from the tray item, the "available" balloon, or Settings.</summary>
    public event Action? UpdateWindowRequested;

    public void RequestUpdateWindow() => UpdateWindowRequested?.Invoke();

    public event Action? ExitRequested;
    public event Action? SettingsRequested;
    public event Action? FinishSetupRequested;

    /// <summary>Settings' "Reset yolime" was confirmed: the app closes its windows, disposes this host, then wipes.</summary>
    public event Action? ResetRequested;

    public void RequestReset() => ResetRequested?.Invoke();

    /// <summary>Applies a lowered retention now rather than at the next start. False if history isn't writable.</summary>
    public bool PruneHistory(int days)
    {
        try
        {
            if (_history.PruneOlderThan(TimeSpan.FromDays(days)) > 0) HistoryChanged?.Invoke(HistoryChange.Reset);
            return true;
        }
        catch (Exception ex) when (ex is HistoryUnavailableException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>The window the user last worked in (not yolime's, not the shell's), with its process identity.</summary>
    public AppWindowId? LastAppWindow
    {
        get { lock (_lastAppGate) return _lastApp; }
    }

    public HistoryStore History => _history;
    public OutputExecutor Executor => _executor;

    /// <summary>History changed: <see cref="HistoryChange.Updated"/> for new or changed rows, Reset for Clear and prunes.</summary>
    public event Action<HistoryChange>? HistoryChanged;

    public event Action? HistoryRequested;

    /// <summary>Settings asked for a page (History's "Change" link for retention).</summary>
    public event Action<string>? SettingsPageRequested;

    public void OpenSettings(string page) => SettingsPageRequested?.Invoke(page);

    /// <summary>Deletes a run and its re-runs, forgets them in paste-last, and tells History. Empty if it couldn't.</summary>
    public async Task<IReadOnlyList<long>> DeleteRunAsync(long id, bool keepReruns = false)
    {
        var deleted = await Pipeline.DeleteRunAsync(id, CancellationToken.None, keepReruns);
        if (deleted.Count > 0)
        {
            LastResult.ForgetRuns(deleted);
            HistoryChanged?.Invoke(HistoryChange.Reset);
        }
        return deleted;
    }

    public async Task<bool> ClearHistoryAsync()
    {
        var ok = await Pipeline.ClearHistoryAsync(LastResult, CancellationToken.None);
        HistoryChanged?.Invoke(HistoryChange.Reset);
        return ok;
    }

    /// <summary>Whether transcription has a key and a valid endpoint, for the tray, Settings and the wizard.</summary>
    public bool SttReady => _stt.CheckReady() == SttReadiness.Ready;

    /// <summary>
    /// The window in front as the hook asks (it picks the hold's starting mode), re-reading the process only when the
    /// window changed. A cache fed by foreground events was stale in the Sandbox (a console window), which started
    /// every hold in Prompt; GetForegroundWindow is cheap enough to ask each time.
    /// </summary>
    private TargetSnapshot ForegroundNow()
    {
        var hwnd = User32.GetForegroundWindow();
        var known = _foreground;
        if (known is not null && known.Hwnd == hwnd) return known;
        var fresh = TargetReader.Light(hwnd);
        _foreground = fresh;
        return fresh;
    }

    private void OnSettingsChanged(YolimeSettings old, YolimeSettings now)
    {
        // Events from two threads can arrive out of order, so act on what is current, not on this event's values.
        var current = Settings.Current;
        if (old.HoldKeyVk != now.HoldKeyVk || old.DoubleTapLatch != now.DoubleTapLatch)
            _hooks.Reconfigure(current.HoldKeyVk, new HoldOptions(DoubleTapLatch: current.DoubleTapLatch, ModeCycling: true));
        // Pausing or resuming history forgets the last result: nothing from before a pause comes back after it, and a
        // run that started on the other side of the switch isn't kept.
        if (old.HistoryPaused != now.HistoryPaused) LastResult.Clear();
        if (!current.KeepMicReady || old.MicDeviceId != now.MicDeviceId) _readyMic?.Suspend();
        // Changed fires on the thread that made the change (the watcher's, for a hand edit); the tray window and its
        // hotkey belong to the UI thread.
        _ui.TryEnqueue(() =>
        {
            if (old.PasteLastHotkey != now.PasteLastHotkey) _tray.RegisterPasteLast(now.PasteLastHotkey);
            if (old.HideOverlayFromCapture != now.HideOverlayFromCapture) _overlay.ApplyCaptureSetting();
            if (old.CheckForUpdates != now.CheckForUpdates) Updates.Reschedule();
            _tray.SetHistoryPaused(now.HistoryPaused);
            RefreshTray();
        });
    }

    /// <summary>The icon and tooltip, recomputed from the app's state in one place.</summary>
    public void RefreshTray()
    {
        var state = TrayState.From(_recording, _tray.HotkeysPaused, SttReady, _micDenied, _targets.SelfElevated, _micOpen);
        if (state.Icon == Yolime.Core.Settings.TrayIconState.Idle && _rewriter.CheckReady() != RewriterReadiness.Ready)
            state = state with { Tooltip = "yolime: ready (no Claude key, so Prompt and Reply copy the raw transcript)" + (_micOpen ? " \u00b7 microphone open" : "") };
        _tray.SetState(state);
    }

    /// <summary>Once per session: the first hold into an admin window says why its results will be copied, not pasted.</summary>
    private void WarnIfElevatedOnce()
    {
        if (Volatile.Read(ref _elevatedWarned) != 0) return;
        var hwnd = User32.GetForegroundWindow();
        _ = Task.Run(() =>
        {
            if (!_targets.BlocksInput(hwnd) || Interlocked.Exchange(ref _elevatedWarned, 1) != 0) return;
            _tray.Show(new Notice(NoticeKind.ElevatedTarget));
        });
    }

    public void ShowAlreadyRunning() => _tray.Show(new Notice(NoticeKind.AlreadyRunning));

    /// <summary>
    /// Gives the user's clipboard back before going away. Runs on the pool, because the caller blocks the UI thread.
    /// The budget covers an idle wait already in flight (up to 2 s, not cancellable) plus clipboard-open retries,
    /// and stays inside the roughly 5 s Windows allows at log-off.
    /// </summary>
    private void FlushClipboard() => Task.Run(_executor.FlushAsync).Wait(TimeSpan.FromSeconds(4));

    /// <summary>
    /// The flush, then the owner window's destruction, which writes a still-unread paste for real. Always both: if the
    /// flush ran out of time, keeping the owner wouldn't save the late restore (the process exits at once), and an
    /// unhandled teardown would leave an offer nobody renders, losing the paste and the original alike. Whichever of
    /// the late restore and the render-all gets the clipboard first, one of them survives.
    /// </summary>
    private void ReleaseClipboard()
    {
        FlushClipboard();
        _clipboardOwner.Dispose();
    }

    public void Dispose()
    {
        Updates.Dispose();
        _micWatcher?.Dispose();
        _ = _readyMic?.DisposeAsync();
        // Wait out a prune already running, so it can't touch the history store after it's disposed.
        if (_pruneTimer is { } timer)
        {
            // Not disposed after a timed-out wait: a prune finishing later would signal a disposed handle on a pool thread.
            var done = new ManualResetEvent(false);
            if (timer.Dispose(done) && done.WaitOne(TimeSpan.FromSeconds(5))) done.Dispose();
        }
        Settings.Dispose();
        _hooks.Dispose();
        ReleaseClipboard();
        _targets.Dispose();
        _overlay.Dispose();
        _context.Dispose();
        _history.Dispose();
        _tray.Dispose();
    }
}

internal enum HistoryChange { Updated, Reset }
