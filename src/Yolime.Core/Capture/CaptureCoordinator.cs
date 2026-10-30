using Yolime.Core.Audio;
using Yolime.Core.Clipboard;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Hotkeys;
using Yolime.Core.Logging;
using Yolime.Core.Modes;
using Yolime.Core.Output;
using Yolime.Core.Overlay;
using Yolime.Core.Pipeline;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Capture;

/// <summary>A running microphone capture. Audio from Start onward is buffered until read; Stop ends the stream.</summary>
public interface ILiveCapture : IAudioSource, IAsyncDisposable
{
    void Stop();

    /// <summary>Completes when the device delivers its first audio (a cold Bluetooth mic can take a second).</summary>
    Task FirstAudio => Task.CompletedTask;

    /// <summary>
    /// When the first buffer arrived, on the coordinator's <see cref="TimeProvider"/> clock; null until one has. Stamped
    /// on delivery, never on stop or error, so a mic that never delivered has no time.
    /// </summary>
    long? FirstAudioAt => null;

    /// <summary>The kind of microphone, for the timing log.</summary>
    MicKind Kind => MicKind.Unknown;

    /// <summary>Whether the mic was already open (kept ready) when the hold started.</summary>
    MicStart MicStart => MicStart.Cold;

    /// <summary>The hold became a dictation (past the tap window): a kept-ready mic stays open for the next one.</summary>
    void Dictating() { }
}

public enum MicStart { Cold, Warm, WarmReopened }

public interface ICaptureDevice
{
    /// <summary>Opens the mic. Throws <see cref="AudioCaptureException"/> if it can't.</summary>
    ILiveCapture Start();
}

public enum SoundKind { Start, Stop, Error, Warning }

public interface ISounds
{
    void Play(SoundKind sound);

    /// <summary>Says which mode a Shift tap switched to without the overlay: 1 beep for Dictate up to 4 for Raw.</summary>
    void PlayModeCue(Mode mode);
}

/// <summary>
/// Reads what a Reply or Prompt is about: the focused element's selection, else a fresh user copy on the clipboard.
/// It never sends keys to the target app. Returns null when there is nothing usable.
/// </summary>
public interface IContextReader
{
    Task<CapturedContext?> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Turns what the hold machine decides into dictation runs. The mic opens at key-down, but nothing touches disk or
/// the pipeline until the hold reaches 300 ms (until then it may be a shortcut like Ctrl+C). Problems found at
/// key-down are only shown if the hold turns out to be a dictation.
/// </summary>
public sealed class CaptureCoordinator(
    DictationPipeline pipeline,
    ICaptureDevice device,
    ITargetReader targets,
    ITranscriptionSessionFactory stt,
    IHistoryStore history,
    INoticeSink notices,
    ISounds sounds,
    Action<Action<HoldStateMachine>> onMachine,
    TimeProvider time,
    ILog log,
    IContextReader? contextReader = null,
    Func<YolimeSettings>? settings = null,
    Action<OverlayInput>? overlay = null,
    Func<string?, string>? displayName = null,
    LastResult? lastResult = null,
    IOutputSink? output = null,
    Action? holdStarted = null)
{
    // With an overlay, run notices go to it rather than the tray: the App routes them to a balloon only when the
    // overlay can't be seen (a full-screen app).
    private readonly Lock _gate = new();
    private readonly NoticeLimiter _limiter = new(TimeSpan.FromSeconds(30));
    private readonly Dictionary<Guid, TargetSnapshot?> _startSnapshots = [];
    private Capture? _current;
    private Capture? _processing;
    private Task _lastRun = Task.CompletedTask;
    private int _generation;

    /// <summary>Raised whenever recording starts or stops, for the tray icon.</summary>
    public event Action<bool>? RecordingChanged;

    /// <summary>Raised when a run finishes, with its result (the E2E build records these).</summary>
    public event Action<Pipeline.RunResult>? RunFinished;

    /// <summary>Raised for every notice shown (the E2E build records these).</summary>
    public event Action<Notice>? NoticeShown;

    /// <summary>A bare tap of the hold key, too short to record: an error card on screen may turn it into Retry.</summary>
    public event Action? HotkeyTapped;

    /// <summary>A run, retry or re-run finished, so history has a new or changed row (the History window refreshes).</summary>
    public event Action? HistoryChanged;

    private long NowMs => (long)time.GetTimestamp() * 1000 / time.TimestampFrequency;

    private void Report(OverlayInput input) => overlay?.Invoke(input);

    private string AppOf(Capture c)
    {
        var process = c.Snapshot is { IsCompletedSuccessfully: true } s ? s.Result.ProcessName : null;
        return displayName?.Invoke(process) ?? process ?? "";
    }

    /// <summary>
    /// A hold is recording or its run is still working (an open preview or pending paste included): an update waits
    /// for it rather than cutting it off.
    /// </summary>
    public bool InProgress
    {
        get
        {
            lock (_gate) return _current is not null || _processing is not null;
        }
    }

    /// <summary>The target captured at key-down for a run, for <see cref="OutputExecutor"/>.</summary>
    public TargetSnapshot? StartSnapshot(Guid runId)
    {
        lock (_gate) return _startSnapshots.GetValueOrDefault(runId);
    }

    /// <summary>Called on the hook thread for every effect; does only quick work there.</summary>
    public void Handle(HoldEffect effect)
    {
        lock (_gate)
        {
            switch (effect)
            {
                case StartCapture s: Start(s.Mode); break;
                case ShowRecording: Show(); break;
                case Latched:
                    if (_current is { } latched) latched.Latched = true;
                    Show();
                    break;
                case CycleMode c when _current is { } cur:
                    cur.Mode = c.Next;
                    Report(new ModeChanged(c.Next));
                    var current = settings?.Invoke();
                    if (current is null || (current.Sounds && current.ModeCycleCues)) sounds.PlayModeCue(c.Next);
                    if (cur.Shown) ReadContextIfNeeded(cur);
                    break;
                case Commit c: CommitCurrent(c); break;
                case Discard d: DiscardCurrent(d.Reason, d.FromWatchdog); break;
                case CancelProcessing:
                    // The run's result decides the notice: only a cancel that kept the recording says so.
                    _processing?.Cts.Cancel();
                    break;
                case RejectBusy:
                    Show(new Notice(NoticeKind.Busy, _processing?.Mode));
                    break;
                case CapWarning w when w.SecondsLeft == 10:
                    sounds.Play(SoundKind.Warning);
                    // With the notch, the countdown shows on Listening; without it, a balloon says so.
                    if (overlay is not null) Report(new CapCountdown(10, NowMs));
                    else Show(new Notice(NoticeKind.CapWarning, Seconds: 10));
                    break;
            }
        }
    }

    /// <summary>
    /// Retries the newest failed run (tray menu): a kept recording is transcribed again, and a Prompt or Reply whose
    /// rewrite failed is rewritten from its transcript. Never pastes: the result goes to the clipboard.
    /// </summary>
    public async Task RetryLastAsync(CancellationToken ct)
    {
        var row = history.LatestRetryable();
        if (row is null)
        {
            Show(new Notice(NoticeKind.RetryUnavailable));
            return;
        }
        await RetryAsync(row, ct);
    }

    /// <summary>Retries one run, by its history id (the overlay's Retry button retries the run on its card).</summary>
    public async Task RetryAsync(long historyId, CancellationToken ct)
    {
        if (history.Get(historyId) is not { } row)
        {
            Show(new Notice(NoticeKind.RetryUnavailable));
            return;
        }
        await RetryAsync(row, ct);
    }

    /// <summary>
    /// Pastes the last result again (the paste-last chord), into whatever has focus now. It goes through the executor
    /// like any run, with the current target as both start and end, so password fields, elevated windows and
    /// terminals get the same rules.
    /// </summary>
    public async Task PasteLastAsync(CancellationToken ct)
    {
        if (output is null || lastResult is null) return;
        string text;
        Mode mode;
        lock (_gate)
        {
            if (IsBusyLocked()) return;
            if (!lastResult.TryGet(out text, out mode))
            {
                Show(new Notice(NoticeKind.PasteLastUnavailable));
                return;
            }
        }
        await PasteTextAsync(text, mode, ct);
    }

    /// <summary>
    /// Pastes text into whatever has focus now (paste-last, History's Re-insert). The current target is both the start
    /// and the end, so password fields, elevated windows, terminals and yolime's own windows get the usual rules.
    /// Null when busy or there's nowhere to deliver.
    /// </summary>
    /// <param name="expectHwnd">
    /// When set (Re-insert), the text goes only into that window: if another took the foreground since it was brought
    /// forward, nothing is delivered and <see cref="DeliveryKind.None"/> comes back so the caller can copy instead.
    /// </param>
    public async Task<DeliveryKind?> PasteTextAsync(string text, Mode mode, CancellationToken ct, nint expectHwnd = 0)
    {
        if (output is null) return null;
        lock (_gate)
        {
            if (IsBusyLocked()) return null;
        }
        var runId = Guid.NewGuid();
        var now = await Task.Run(targets.Snapshot, ct);
        if (expectHwnd != 0 && now.Hwnd != expectHwnd) return DeliveryKind.None;
        lock (_gate) _startSnapshots[runId] = now;
        try
        {
            return await output.DeliverAsync(new OutputItem(text, DeliveryIntent.Paste, PreviewRequested: false, mode, runId), ct);
        }
        finally
        {
            lock (_gate) _startSnapshots.Remove(runId);
        }
    }

    /// <summary>
    /// Re-runs a kept transcript in another mode (History): a new row, copied to the clipboard, kept as the last
    /// result like a retry. Null if it couldn't start (busy, paused, or no transcript), after saying why.
    /// </summary>
    public async Task<RunResult?> RerunAsync(long historyId, Mode asMode, CancellationToken ct)
    {
        lock (_gate)
        {
            if (IsBusyLocked()) return null;
        }
        try
        {
            var progress = overlay is null ? null : new OverlayProgress(overlay, time);
            var started = lastResult?.Generation ?? 0;
            var result = await pipeline.RerunAsync(historyId, asMode, progress, ct);
            lastResult?.Record(result, started);
            if (overlay is not null) Report(new RunFinished(result, Notices.For(result), "", NowMs));
            else if (Notices.For(result) is { } n) Show(n);
            RaiseHistoryChanged();
            return result;
        }
        catch (InvalidOperationException)
        {
            Show(new Notice(NoticeKind.RetryUnavailable));
            return null;
        }
    }

    /// <summary>A subscriber's exception (a window) must never reach the run that finished.</summary>
    private void RaiseHistoryChanged()
    {
        try
        {
            HistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            log.Write(LogEvent.UnhandledException, LogField.Error(ex));
        }
    }

    /// <summary>One thing at a time on the notch; says Busy when a hold is recording or working. Call under _gate.</summary>
    private bool IsBusyLocked()
    {
        if (_current is null && _processing is null) return false;
        Show(new Notice(NoticeKind.Busy, _processing?.Mode ?? _current?.Mode));
        return true;
    }

    private async Task RetryAsync(HistoryRow row, CancellationToken ct)
    {
        lock (_gate)
        {
            // One thing at a time on the notch: a retry while a hold is recording or working would mix their cards.
            if (_current is not null || _processing is not null)
            {
                Show(new Notice(NoticeKind.Busy, _processing?.Mode ?? _current?.Mode));
                return;
            }
        }
        try
        {
            var progress = overlay is null ? null : new OverlayProgress(overlay, time);
            var started = lastResult?.Generation ?? 0;
            var result = row.PendingAudioFile is null
                ? await pipeline.RunFromTranscriptAsync(row.Id, progress, ct)
                : await pipeline.RetryAsync(row.Id, null, progress, ct);
            lastResult?.Record(result, started);
            if (overlay is not null) Report(new RunFinished(result, Notices.For(result), displayName?.Invoke(row.ProcessName) ?? row.ProcessName ?? "", NowMs));
            else if (Notices.For(result) is { } n) Show(n);
            RaiseHistoryChanged();
        }
        catch (InvalidOperationException)
        {
            Show(new Notice(NoticeKind.RetryUnavailable));
        }
    }

    private void Start(Mode mode)
    {
        // Connection warm-up runs while the user speaks; it never delays the hold.
        try { holdStarted?.Invoke(); } catch (Exception ex) { log.Write(LogEvent.UnhandledException, LogField.Error(ex)); }
        var capture = new Capture(++_generation, mode) { ResultGeneration = lastResult?.Generation ?? 0, StartedAtMs = NowMs, StartedAt = time.GetTimestamp() };
        _current = capture;
        var readiness = stt.CheckReady();
        if (readiness != SttReadiness.Ready)
        {
            capture.PendingError = new Notice(readiness == SttReadiness.MissingKey ? NoticeKind.NoKey : NoticeKind.EndpointInvalid);
            return;
        }
        try
        {
            capture.Live = device.Start();
        }
        catch (AudioCaptureException ex)
        {
            capture.PendingError = new Notice(ex.Error switch
            {
                CaptureError.AccessDenied => NoticeKind.MicDenied,
                CaptureError.NoDevice => NoticeKind.NoMic,
                _ => NoticeKind.MicLost,
            });
            return;
        }
        catch (Exception ex)
        {
            // Any other failure opening the mic is still a mic problem, never a run with no audio behind it.
            log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Capture), LogField.Error(ex));
            capture.PendingError = new Notice(ex is UnauthorizedAccessException ? NoticeKind.MicDenied : NoticeKind.NoMic);
            return;
        }
        // UIA can take a while on a cold app: read the target in parallel with the mic, never before it.
        capture.Snapshot = Task.Run(targets.Snapshot);
    }

    private void Show()
    {
        if (_current is not { } c || c.Shown) return;
        c.Shown = true;
        if (c.PendingError is { } error)
        {
            sounds.Play(SoundKind.Error);
            Show(error);
            AbortCurrent(c);
            return;
        }
        // A real dictation, not a tap or a shortcut: a kept-ready mic stays open for the next one.
        c.Live?.Dictating();
        // One reading of readiness for both: the card must agree with whether a "flowing" report will follow.
        var micReady = PlayStartWhenAudioFlows(c);
        RecordingChanged?.Invoke(true);
        Report(new RecordingStarted(c.Mode, AppOf(c), c.Latched, NowMs, c.RunId, micReady));
        ReadContextIfNeeded(c);
        c.Run = RunAsync(c);
    }

    /// <summary>
    /// The start cue means "speak now", so it waits for the mic's first audio (up to two seconds, which covers a Bluetooth
    /// headset switching profiles; past that it plays anyway,
    /// since a silent wait is worse). Only for the hold still in progress. The card hears about it too.
    /// </summary>
    /// <returns>Whether the mic had already delivered: the card then starts ready, and no flowing report follows.</returns>
    private bool PlayStartWhenAudioFlows(Capture c)
    {
        if (c.Live?.FirstAudio is not { IsCompleted: false } first)
        {
            sounds.Play(SoundKind.Start);
            return true;
        }
        _ = first.WaitAsync(TimeSpan.FromSeconds(2), time).ContinueWith(t =>
        {
            // The cap firing is a TimeoutException by design: observe it, or it's logged as an unhandled exception.
            _ = t.Exception;
            lock (_gate)
            {
                if (_current != c) return;
                sounds.Play(SoundKind.Start);
                Report(new AudioFlowing(NowMs, t.IsCompletedSuccessfully, c.RunId));
            }
        }, TaskScheduler.Default);
        return false;
    }

    /// <summary>
    /// Starts the context read the first time the hold is in Prompt or Reply: at 300 ms, or on the cycle into one.
    /// Dictate and Raw never read it. It runs beside the recording; the pipeline waits for it only before the rewrite.
    /// </summary>
    private void ReadContextIfNeeded(Capture c)
    {
        if (c.ContextStarted || contextReader is null || c.Mode is not (Mode.Prompt or Mode.Reply)) return;
        c.ContextStarted = true;
        var reader = contextReader;
        _ = Task.Run(async () =>
        {
            try
            {
                var context = await reader.ReadAsync(c.Cts.Token);
                c.Context.TrySetResult(context);
                ReportContext(context);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException) log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Rewrite), LogField.Error(ex));
                c.Context.TrySetResult(null);
            }
        });
    }

    /// <summary>
    /// What the notch says the run is about. The snippet goes through the same filter the run will use, so a key
    /// on the clipboard is never displayed even though it's never sent.
    /// </summary>
    private void ReportContext(CapturedContext? context)
    {
        if (overlay is null) return;
        if (context is null || ContextFilter.Apply(context.Text, context.Source, sensitiveMarkers: false) is not { } filtered)
        {
            Report(new ContextShown(null, null, 0, Skipped: false));
            return;
        }
        Report(filtered.Verdict == ContextVerdict.Rejected
            ? new ContextShown(null, null, 0, Skipped: true)
            : new ContextShown(filtered.Text, context.Source, context.AgeMs, Skipped: false));
    }

    private async Task RunAsync(Capture c)
    {
        var previous = _lastRun;
        var done = new TaskCompletionSource();
        _lastRun = done.Task;
        try
        {
            // A previous run may still be unwinding from a cancel; one run at a time.
            await previous;
            var target = c.Snapshot is null ? null : await c.Snapshot;
            if (overlay is not null && target is not null) Report(new AppIdentified(displayName?.Invoke(target.ProcessName) ?? target.ProcessName));
            if (target?.IsPassword == true)
            {
                // Nothing said into a password field is recorded, uploaded or kept. The user may already have let go
                // (a slow UIA read), in which case the machine is waiting in Processing and must be released too.
                lock (_gate)
                {
                    if (_current == c) AbortCurrent(c);
                    else if (_processing == c)
                    {
                        _processing = null;
                        RecordingChanged?.Invoke(false);
                        onMachine(m => m.SetProcessing(false));
                    }
                }
                // The notch must not stay on Listening or Working: the notice below can be rate-limited away.
                Report(new Dismissed());
                Show(new Notice(NoticeKind.PasswordRefused));
                return;
            }
            lock (_gate) _startSnapshots[c.RunId] = target;
            var progress = overlay is null ? null : new OverlayProgress(overlay, time);
            // Read when the run finishes: a cold Bluetooth mic's first buffer can come after the pipeline has started.
            var live = c.Live!;
            MicTiming Mic() => new(live.FirstAudioAt is { } at ? (long)time.GetElapsedTime(c.StartedAt, at).TotalMilliseconds : null, live.Kind, live.MicStart);
            var request = new RunRequest(live, c.Mode, target?.ProcessName, c.Intent.Task, progress, c.RunId, c.Context.Task, Profiles.For(target), Mic);
            var result = await pipeline.RunAsync(request, c.Cts.Token);
            OnCompleted(c, result);
        }
        catch (Exception ex)
        {
            log.Write(LogEvent.UnhandledException, LogField.Error(ex));
            Report(new Dismissed());
            OnCompleted(c, null);
        }
        finally
        {
            lock (_gate) _startSnapshots.Remove(c.RunId);
            if (c.Live is { } live) await live.DisposeAsync();
            done.SetResult();
        }
    }

    private void CommitCurrent(Commit commit)
    {
        if (_current is not { } c) return;
        // A watchdog commit can arrive without a ShowRecording first (the key-up was missed past 300 ms).
        if (!c.Shown) Show();
        if (c.PendingError is not null) return;
        c.Live?.Stop();
        c.Intent.TrySetResult(new RunIntent(c.Mode, commit.ShiftHeld));
        if (!c.ContextStarted) c.Context.TrySetResult(null); // never in Prompt or Reply: nothing to wait for
        _processing = c;
        _current = null;
        sounds.Play(SoundKind.Stop);
        RecordingChanged?.Invoke(false);
        Report(new RecordingEnded(NowMs));
        if (commit.Reason == CommitReason.Cap) Show(new Notice(NoticeKind.CapReached));
    }

    private void DiscardCurrent(DiscardReason reason, bool fromWatchdog = false)
    {
        if (_current is not { } c) return;
        _current = null;
        // Content-free, and the only trace of a hold that ends before it shows: a misread tap or a flash would be invisible.
        log.Write(LogEvent.HoldDiscarded, LogField.Tag("reason", reason), LogField.Count("watchdog", fromWatchdog ? 1 : 0),
            LogField.Ms("ms", NowMs - c.StartedAtMs));
        c.Live?.Stop();
        c.Cts.Cancel();
        c.Intent.TrySetCanceled();
        c.Context.TrySetResult(null);
        if (c.Run is null && c.Live is { } live) _ = live.DisposeAsync().AsTask();
        // A bare tap: nothing was recorded, and an error card may be waiting for exactly this (Retry by tap).
        // With double-tap latch on, a tap may be the first of a double tap, so it never retries. Nor does a tap the
        // watchdog inferred: that can be a real hold misread at key-down, and it mustn't retry a card the user left.
        if (reason == DiscardReason.ShortTap && !fromWatchdog && !c.Shown && settings?.Invoke().DoubleTapLatch != true) HotkeyTapped?.Invoke();
        if (c.Shown)
        {
            RecordingChanged?.Invoke(false);
            Report(new Dismissed());
            if (reason is DiscardReason.OtherInput or DiscardReason.ForegroundChanged) Show(new Notice(NoticeKind.Discarded));
        }
    }

    /// <summary>The capture can't go on (an error at key-down, a password field): reset the machine too.</summary>
    private void AbortCurrent(Capture c)
    {
        _current = null;
        c.Live?.Stop();
        c.Cts.Cancel();
        c.Intent.TrySetCanceled();
        c.Context.TrySetResult(null);
        RecordingChanged?.Invoke(false);
        var generation = c.Generation;
        onMachine(m =>
        {
            lock (_gate)
            {
                // Only if no newer hold has started since.
                if (_generation == generation) m.Abort();
            }
        });
    }

    private void OnCompleted(Capture c, RunResult? result)
    {
        if (result is not null)
        {
            lastResult?.Record(result, c.ResultGeneration);
            RunFinished?.Invoke(result);
            RaiseHistoryChanged();
        }
        var notice = result is null ? null : Notices.For(result);
        if (notice is not null && notice.Kind != NoticeKind.Cancelled) sounds.Play(SoundKind.Error);
        if (overlay is not null && result is { Outcome: RunOutcome.Busy })
        {
            // Turned away by a run already in progress (a retry): its result has no run id, so the notch would stay
            // on this hold's Listening card. Clear it and say why.
            Report(new Dismissed());
            Show(new Notice(NoticeKind.Busy, result.BusyWith?.Mode));
        }
        else if (overlay is not null && result is not null)
        {
            Report(new RunFinished(result, notice, AppOf(c), NowMs));
            if (notice is not null) NoticeShown?.Invoke(notice);
        }
        else if (notice is not null) Show(notice);
        lock (_gate)
        {
            if (_processing == c)
            {
                _processing = null;
                onMachine(m => m.SetProcessing(false));
            }
            else if (_current == c)
            {
                // The run ended while the key was still held (mic lost, an error): release the hold.
                AbortCurrent(c);
            }
            // Otherwise it was discarded and a newer hold owns the machine: leave it alone.
        }
    }

    private void Show(Notice n)
    {
        if (!_limiter.ShouldShow(n, NowMs)) return;
        if (overlay is not null) Report(new NoticeOnly(n, NowMs));
        else notices.Show(n);
        NoticeShown?.Invoke(n);
    }

    private sealed class Capture(int generation, Mode mode)
    {
        public int Generation { get; } = generation;

        /// <summary><see cref="LastResult.Generation"/> at key-down: a Clear since then keeps this result out.</summary>
        public long ResultGeneration { get; init; }

        /// <summary>The coordinator's clock at key-down, for the discard log.</summary>
        public long StartedAtMs { get; init; }

        /// <summary>The same moment as a <see cref="TimeProvider"/> timestamp, for the mic's start time.</summary>
        public long StartedAt { get; init; }
        public Guid RunId { get; } = Guid.NewGuid();
        public Mode Mode { get; set; } = mode;
        public ILiveCapture? Live { get; set; }
        public Notice? PendingError { get; set; }
        public Task<TargetSnapshot>? Snapshot { get; set; }
        public TaskCompletionSource<RunIntent> Intent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CapturedContext?> Context { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ContextStarted { get; set; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? Run { get; set; }
        public bool Shown { get; set; }
        public bool Latched { get; set; }
    }
}
