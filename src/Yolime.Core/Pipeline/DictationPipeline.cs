using System.Text;
using Yolime.Core.Audio;
using Yolime.Core.Clipboard;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Logging;
using Yolime.Core.Modes;
using Yolime.Core.Output;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Pipeline;

/// <summary>
/// One dictation, end to end: capture → STT → rewrite → output, with the history row and pending WAV kept so that
/// nothing the user said is lost to a crash, a failed upload or a cancelled delivery. One run at a time.
/// </summary>
public sealed class DictationPipeline(
    ITranscriptionSessionFactory stt,
    IRewriter? rewriter,
    IOutputSink output,
    IHistoryStore history,
    PendingAudioStore pending,
    ILog log,
    ITimingLog timings,
    Func<YolimeSettings> settings,
    TimeProvider time)
{
    /// <summary>How long a committed recording may keep draining after a cancel before the read is abandoned.</summary>
    public static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    /// <summary>The longest pause between streamed deltas, once the first has arrived.</summary>
    public static readonly TimeSpan RewriteGap = TimeSpan.FromSeconds(5);

    /// <summary>The longest a whole rewrite may take.</summary>
    public static readonly TimeSpan RewriteTotal = TimeSpan.FromSeconds(60);

    /// <summary>How long the rewrite waits for a context read that is still running (it normally finished long ago).</summary>
    public static readonly TimeSpan ContextWait = TimeSpan.FromSeconds(1);

    private int _busy;
    private volatile CurrentRun? _current;

    // The nested Run reaches the dependencies through these; nested types can't see primary-constructor parameters.
    private TimeProvider Time => time;
    private ITranscriptionSessionFactory Stt => stt;
    private ILog Log => log;
    private IHistoryStore History => history;
    private PendingAudioStore Pending => pending;
    private ITimingLog Timings => timings;

    public CurrentRun? Current => _current;

    public async Task<RunResult> RunAsync(RunRequest request, CancellationToken ct)
    {
        if (!TryEnter(request.InitialMode, out var busy)) return busy;
        Run? run = null;
        try
        {
            run = new Run(this, request.RunId ?? Guid.NewGuid(), request.InitialMode, request.Progress)
            {
                HistoryPaused = settings().HistoryPaused,
                Mic = request.Mic,
            };
            return await RunCoreAsync(run, request, ct);
        }
        catch (Exception ex) when (run is not null && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return await InterruptedAsync(run, ex);
        }
        finally
        {
            // Paused history keeps nothing, whatever the outcome: the recording goes too, even though a failed run
            // then can't be retried.
            if (run is { HistoryPaused: true, Recording: { } recording })
            {
                try { await recording.DeleteAsync(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Exit();
        }
    }

    /// <summary>
    /// Transcribes a kept recording again, into the same history row. Skips the speech gate (this is "send anyway"
    /// for NoSpeech) and never deletes the audio before delivery has finished.
    /// </summary>
    public async Task<RunResult> RetryAsync(long historyId, Mode? asMode, IProgress<PipelineEvent>? progress, CancellationToken ct)
    {
        if (!TryEnter(asMode ?? Mode.Raw, out var busy)) return busy;
        try
        {
            var row = history.Get(historyId);
            if (row is null || row.PendingAudioFile is null || row.AudioExpired)
                throw new InvalidOperationException("this run has no audio to retry");
            if (!File.Exists(pending.PathOf(row.PendingAudioFile)))
            {
                // The audio vanished (deleted by hand, or a cleanup whose row update failed): say so, keep the outcome.
                try { history.MarkAudioExpired([row.PendingAudioFile]); } catch (HistoryUnavailableException) { }
                throw new InvalidOperationException("this run's audio is gone");
            }

            var run = new Run(this, Guid.NewGuid(), asMode ?? row.Mode, progress) { HistoryId = historyId, HistoryWritten = true, WavName = row.PendingAudioFile, Previous = row };
            try
            {
                if (Ready(run) is { } notReady) return notReady;
                run.TryHistory(() => history.Update(historyId, r => r with { Outcome = RunOutcome.InProgress, Mode = run.Mode }));

                var pcm = WavFile.ReadPcm(pending.PathOf(row.PendingAudioFile));
                await using var session = stt.Create(new SttSessionOptions(settings().Vocabulary, TrimBelow: (float)settings().SilenceRms));
                session.PushAudio(pcm);
                run.AudioBytes = pcm.Length;
                run.MarkAudioEnd();
                return await TranscribeAndDeliverAsync(run, session, new RunIntent(run.Mode, false), RowContext(row), RowProfile(row), ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                return await InterruptedAsync(run, ex);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Rewrites a kept transcript again (a Prompt or Reply whose rewrite failed), into the same row. No STT: the
    /// audio is already gone. The result goes to the clipboard, since there is no start target to paste into.
    /// </summary>
    public async Task<RunResult> RunFromTranscriptAsync(long historyId, IProgress<PipelineEvent>? progress, CancellationToken ct)
    {
        if (!TryEnter(Mode.Raw, out var busy)) return busy;
        try
        {
            var row = history.Get(historyId);
            if (row?.RawText is not { Length: > 0 } raw) throw new InvalidOperationException("this run has no transcript to retry");
            var run = new Run(this, Guid.NewGuid(), row.Mode, progress) { HistoryId = historyId, HistoryWritten = true, Previous = row, RawText = raw };
            try
            {
                var readiness = rewriter?.CheckReady();
                if (readiness != RewriterReadiness.Ready)
                {
                    run.RewriteError = readiness == RewriterReadiness.MissingKey ? RewriteFailure.MissingKey : RewriteFailure.Unavailable;
                    run.Done(Stage.Rewrite, StageStatus.Failed);
                    return run.Finish(RunOutcome.RewriteFailed);
                }
                run.TryHistory(() => history.Update(historyId, r => r with { Outcome = RunOutcome.InProgress }));
                run.MarkAudioEnd();
                run.Done(Stage.Stt, StageStatus.Skipped);
                return await RewriteAndDeliverAsync(run, raw, new RunIntent(run.Mode, false), RowContext(row), RowProfile(row), ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                return await InterruptedAsync(run, ex);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Re-runs a kept transcript in another mode (History). The result is a new row pointing at the original, which is
    /// never changed; it keeps the original's start time, so a re-run can't extend how long the text is kept. Context
    /// is carried only into modes that send it. The result goes to the clipboard.
    /// </summary>
    public async Task<RunResult> RerunAsync(long historyId, Mode asMode, IProgress<PipelineEvent>? progress, CancellationToken ct)
    {
        if (!TryEnter(asMode, out var busy)) return busy;
        try
        {
            if (settings().HistoryPaused) throw new InvalidOperationException("history is paused");
            var source = history.Get(historyId);
            if (source?.RawText is not { Length: > 0 } raw) throw new InvalidOperationException("this run has no transcript to re-run");
            var usesContext = asMode is Mode.Prompt or Mode.Reply;
            var copy = new HistoryRow
            {
                RunId = Guid.NewGuid(),
                StartedUtc = source.StartedUtc,
                Mode = asMode,
                ProcessName = source.ProcessName,
                RawText = raw,
                Context = usesContext ? source.Context : null,
                ContextSource = usesContext ? source.ContextSource : null,
                // Chains stay flat: a re-run of a re-run points at the original, so deleting it takes them all.
                RerunOf = source.RerunOf ?? source.Id,
            };
            var run = new Run(this, copy.RunId, asMode, progress) { RawText = raw, ClipboardOnly = true };
            run.HistoryId = run.TryHistory(() => history.Insert(copy));
            run.HistoryWritten = run.HistoryId is not null;
            try
            {
                run.MarkAudioEnd();
                run.Done(Stage.Stt, StageStatus.Skipped);
                var context = usesContext ? RowContext(source) : null;
                return await RewriteAndDeliverAsync(run, raw, new RunIntent(asMode, false), context, RowProfile(source), ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                return await InterruptedAsync(run, ex);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Deletes a run and its re-runs (History). Waits for a run in progress and holds the gate meanwhile, like Clear,
    /// so a re-run can't copy the row back after its delete. Returns the ids deleted; empty if history isn't writable.
    /// </summary>
    public async Task<IReadOnlyList<long>> DeleteRunAsync(long historyId, CancellationToken ct, bool keepReruns = false)
    {
        while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) await Task.Delay(50, ct);
        try
        {
            return history.DeleteRun(historyId, keepReruns);
        }
        catch (HistoryUnavailableException)
        {
            return [];
        }
        finally
        {
            Exit();
        }
    }

    private static Task<CapturedContext?>? RowContext(HistoryRow row) => row.Context is null ? null
        : Task.FromResult<CapturedContext?>(new CapturedContext(row.Context, row.ContextSource ?? ContextSource.Selection, 0));

    /// <summary>Only the process name survives in history, so a retry gets that app's profile without its window class.</summary>
    private static Profile RowProfile(HistoryRow row) => row.ProcessName is null ? Profile.Neutral
        : Profiles.For(new TargetSnapshot(0, 0, row.ProcessName, "", null, null, false, null, false));

    /// <summary>
    /// A failure nobody anticipated (a bug, a full disk). The audio stays, and the row is left retryable: a new run
    /// becomes Interrupted, and a retry goes back to how it was. The run is still finished, so it is logged and timed.
    /// </summary>
    private async Task<RunResult> InterruptedAsync(Run run, Exception ex)
    {
        log.Write(LogEvent.StageFailed, LogField.Tag("stage", _current?.Stage ?? Stage.Capture), LogField.Error(ex));
        if (run.Recording is { } recording)
        {
            try
            {
                await recording.FinaliseAsync();
                await recording.DisposeAsync();
            }
            catch (Exception)
            {
                // Best effort: the WAV is readable without a finalised header.
            }
        }
        if (run.Previous is { } previous)
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = previous.Outcome, Mode = previous.Mode }));
        else
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = RunOutcome.Interrupted, Stages = run.Stages }));
        return run.Finish(RunOutcome.Interrupted);
    }

    private async Task<RunResult> RunCoreAsync(Run run, RunRequest request, CancellationToken ct)
    {
        if (Ready(run) is { } notReady) return notReady;

        run.Report(new StageStarted(Stage.Capture));
        var recording = pending.Begin(run.Id, run.HistoryPaused);
        run.Recording = recording;
        run.WavName = recording.FileName;
        if (!run.HistoryPaused) run.HistoryId = run.TryHistory(() => history.Insert(new HistoryRow
        {
            RunId = run.Id,
            StartedUtc = run.StartedUtc,
            Mode = request.InitialMode,
            ProcessName = request.ProcessName,
            PendingAudioFile = recording.FileName,
        }));
        run.HistoryWritten = run.HistoryId is not null;
        log.Write(LogEvent.RunStarted, LogField.Tag("mode", request.InitialMode));

        // Live: the audio arrives while the key is held, so the session may stream it (Retry's finished buffer never does).
        await using var session = stt.Create(new SttSessionOptions(settings().Vocabulary, TrimBelow: (float)settings().SilenceRms, Live: true));
        var gate = new SpeechGate((float)settings().SilenceRms);
        var captureStart = time.GetTimestamp();
        RunIntent intent;
        // Once the hold is committed (the intent resolved), a cancel no longer stops the read: the buffered audio is
        // drained and kept, so a tap right after release loses nothing (Bryan, 2026-10-28). The drain is bounded: a
        // device that never reports "stopped" must not leave the tap unable to free the app.
        using var captureCts = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        using var forwardCancel = ct.Register(() =>
        {
            if (request.Intent.IsCompletedSuccessfully) captureCts.CancelAfter(DrainLimit);
            else captureCts.Cancel();
        });
        try
        {
            await foreach (var chunk in request.Audio.ReadAsync(captureCts.Token))
            {
                await recording.AppendAsync(chunk.Pcm);
                session.PushAudio(chunk.Pcm);
                gate.Add(chunk);
                run.Report(new CaptureLevel(chunk.Rms, (long)time.GetElapsedTime(captureStart).TotalMilliseconds));
            }
            await recording.FinaliseAsync();
            await recording.DisposeAsync();
            run.AudioBytes = recording.DataBytes;
            run.CaptureMs = (long)time.GetElapsedTime(captureStart).TotalMilliseconds;
            run.MarkAudioEnd();
            intent = await request.Intent.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (AudioCaptureException ex)
        {
            run.CaptureError = ex.Error;
            run.Done(Stage.Capture, StageStatus.Failed);
            if (recording.DataBytes == 0)
            {
                await recording.DeleteAsync();
                run.DeleteRow();
                return run.Finish(RunOutcome.CaptureFailed);
            }
            await recording.FinaliseAsync();
            await recording.DisposeAsync();
            run.AudioBytes = recording.DataBytes;
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = RunOutcome.CaptureFailed, CaptureError = ex.Error }));
            return run.Finish(RunOutcome.CaptureFailed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Done(Stage.Capture, StageStatus.Cancelled);
            if (request.Intent.IsCompletedSuccessfully && recording.DataBytes > 0)
            {
                // Committed, then cancelled: keep the recording for Retry.
                await recording.FinaliseAsync();
                await recording.DisposeAsync();
                run.AudioBytes = recording.DataBytes;
                run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = RunOutcome.Cancelled, Stages = run.Stages }));
                return run.Finish(RunOutcome.Cancelled);
            }
            // Discarded while still recording (Esc, a short tap, other input): nothing is kept.
            await recording.DeleteAsync();
            run.DeleteRow();
            return run.Finish(RunOutcome.Cancelled);
        }

        run.Mode = intent.Mode;
        run.Done(Stage.Capture, StageStatus.Ok);
        run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Mode = intent.Mode }));

        if (!gate.HeardSpeech)
        {
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = RunOutcome.NoSpeech, Stages = run.Stages }));
            return run.Finish(RunOutcome.NoSpeech);
        }
        return await TranscribeAndDeliverAsync(run, session, intent, request.Context, request.Profile, ct);
    }

    private async Task<RunResult> TranscribeAndDeliverAsync(Run run, ITranscriptionSession session, RunIntent intent,
        Task<CapturedContext?>? pendingContext, Profile profile, CancellationToken ct)
    {
        // --- STT ---
        run.SetStage(Stage.Stt);
        run.Report(new StageStarted(Stage.Stt));
        var sttStartMs = run.SinceAudioEnd();
        SttResult result;
        using (var timeout = new CancellationTokenSource(SttTimeout.For(run.AudioBytes), time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
        {
            try
            {
                result = await session.CompleteAsync(linked.Token);
            }
            catch (SttException ex)
            {
                run.Timing = run.Timing with { SttUpload = ex.Upload };
                return SttFailed(run, ex.Error, StageStatus.Failed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Cancelled(run, Stage.Stt);
            }
            catch (OperationCanceledException)
            {
                return SttFailed(run, SttError.Timeout, StageStatus.TimedOut);
            }
        }
        run.Timing = run.Timing with
        {
            SttBodySentMs = sttStartMs + result.BodySentMs,
            SttFirstByteMs = sttStartMs + result.FirstByteMs,
            SttDoneMs = run.SinceAudioEnd(),
            NewConnection = result.NewConnection,
            SttUploadBytes = result.UploadBytes,
            SttUpload = result.Upload,
        };
        var raw = result.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // The gate heard sound but STT found no words: keep the audio so Retry can send it again.
            run.Done(Stage.Stt, StageStatus.Ok);
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = RunOutcome.NoSpeech, Stages = run.Stages }));
            return run.Finish(RunOutcome.NoSpeech);
        }
        run.RawText = raw;
        run.Report(new TranscriptReady(raw));
        run.Done(Stage.Stt, StageStatus.Ok);
        run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { RawText = raw, SttError = null, Mode = run.Mode }));
        // The WAV stays until delivery has finished: a cancel tap at any point after the recording ended keeps it for
        // Retry (Bryan, 2026-10-28), and a crash before delivery leaves a retryable recording.
        return await RewriteAndDeliverAsync(run, raw, intent, pendingContext, profile, ct);
    }

    private async Task<RunResult> RewriteAndDeliverAsync(Run run, string raw, RunIntent intent,
        Task<CapturedContext?>? pendingContext, Profile profile, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Cancelled(run, Stage.Rewrite);

        // --- Rewrite ---
        string text;
        // A re-run has no window to paste into: it always goes to the clipboard.
        var deliveryIntent = run.ClipboardOnly ? DeliveryIntent.ClipboardOnly : DeliveryIntent.Paste;
        var quiet = false;
        var def = ModeCatalog.For(run.Mode, settings());
        if (def is null)
        {
            text = raw;
            run.Done(Stage.Rewrite, StageStatus.Skipped);
        }
        else if (run.Mode == Mode.Dictate && ShortDictation.CanSkip(raw))
        {
            // Nothing for the model to fix: local cleanup is instant and free.
            text = RegexCleanup.Apply(raw, profile);
            run.Done(Stage.Rewrite, StageStatus.Skipped);
        }
        else
        {
            run.SetStage(Stage.Rewrite);
            run.Timing = run.Timing with { RewriteModel = RewriteModelKinds.Of(def.Model) };
            var context = def.UsesContext ? await ContextAsync(run, pendingContext) : null;
            var rewritten = await RewriteAsync(run, def, raw, context, profile, ct);
            if (rewritten is null && ct.IsCancellationRequested) return Cancelled(run, Stage.Rewrite);
            if (rewritten is null)
            {
                // Spec fallbacks: Dictate still pastes the raw transcript; Prompt and Reply leave it on the clipboard,
                // and the rewrite notice says so, so the copy itself stays quiet.
                text = raw;
                if (!run.ClipboardOnly) deliveryIntent = def.FallbackIntent;
                quiet = true; // the run's notice says what happened, pasted or copied
                run.FallbackUsed = true;
                run.Done(Stage.Rewrite, StageStatus.FellBack);
            }
            else
            {
                text = OutputText.Normalise(rewritten);
                // The selection is attached by code, verbatim: the model never rewords it into the speaker's voice.
                if (run.Mode == Mode.Prompt) text = PromptOutput.AppendContext(text, context);
                run.Rewritten = true;
                run.Done(Stage.Rewrite, StageStatus.Ok);
            }
        }
        // A cancel that lands as the rewrite finishes must still stop the paste.
        if (ct.IsCancellationRequested) return Cancelled(run, Stage.Output);
        run.Output = text;

        // --- Output --- (the row holds the output before anything is injected)
        run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Output = text, FallbackUsed = run.FallbackUsed, RewriteError = run.RewriteError?.ToString() }));
        run.SetStage(Stage.Output);
        run.MarkPasteStart();
        run.Report(new StageStarted(Stage.Output));
        DeliveryKind delivery;
        try
        {
            var preview = !run.ClipboardOnly && (intent.PreviewRequested || settings().PreviewsAlways(run.Mode));
            delivery = await output.DeliverAsync(new OutputItem(text, deliveryIntent, preview, run.Mode, run.Id, quiet), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Cancelled(run, Stage.Output);
        }
        catch (Exception ex)
        {
            log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Output), LogField.Error(ex));
            run.Done(Stage.Output, StageStatus.Failed);
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = RunOutcome.OutputFailed, Stages = run.Stages }));
            // OutputFailed isn't retryable; once the row holds the output the audio has no use. Without history, the
            // WAV is the only copy, so it stays for Housekeeping to surface.
            if (run.HistoryWritten) run.DeleteWav();
            return run.Finish(RunOutcome.OutputFailed);
        }
        run.Delivery = delivery;
        run.Timing = run.Timing with
        {
            OutputDoneMs = run.SinceAudioEnd(),
            Output = (output as OutputExecutor)?.TakeBreakdown(run.Id),
        };
        run.Done(Stage.Output, delivery == DeliveryKind.Discarded ? StageStatus.Cancelled : StageStatus.Ok);
        run.DeleteWav();

        if (delivery == DeliveryKind.Refused)
        {
            // A password field: the spec says no history row. Blank the text first, so a failed delete leaves no
            // content behind (Housekeeping removes such rows), then flush the WAL.
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { RawText = null, Output = null, Context = null, Delivery = DeliveryKind.Refused }));
            run.DeleteRow(flushWal: true);
            return run.Finish(RunOutcome.Discarded);
        }

        var outcome = delivery == DeliveryKind.Discarded ? RunOutcome.Discarded
            : run.FallbackUsed ? RunOutcome.CompletedWithFallback
            : RunOutcome.Completed;
        run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with
        {
            Outcome = outcome,
            Delivery = delivery,
            Stages = run.Stages,
            StageMs = run.StageMs,
            TotalMs = (long)time.GetElapsedTime(run.StartTimestamp).TotalMilliseconds,
        }));
        return run.Finish(outcome);
    }

    /// <summary>
    /// The context, filtered and stored with the row, or null. A read that failed, hung or found nothing is simply no
    /// context: it never fails the run. A clipboard holding a secret is skipped and the user is told.
    /// </summary>
    private async Task<CapturedContext?> ContextAsync(Run run, Task<CapturedContext?>? pendingContext)
    {
        if (pendingContext is null) return null;
        CapturedContext? captured;
        try
        {
            captured = await pendingContext.WaitAsync(ContextWait, time);
        }
        catch (Exception)
        {
            return null;
        }
        if (captured is null || captured.Text.Trim().Length == 0) return null;
        if (run.Mode == Mode.Prompt && captured.Source == ContextSource.Selection)
        {
            // Prompt never shows the model its context; code attaches the selection as it was, so no redaction or
            // normalising (a hash or "fi" ligature in selected code must come through exactly). Invisible characters
            // still go: they could carry instructions to whichever AI the prompt is sent to.
            var selection = captured with { Text = ContextFilter.StripInvisible(captured.Text) };
            if (selection.Text.Trim().Length == 0) return null;
            run.ContextSource = selection.Source;
            run.ContextChars = selection.Text.Length;
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Context = selection.Text, ContextSource = selection.Source }));
            return selection;
        }
        if (ContextFilter.Apply(captured.Text, captured.Source, sensitiveMarkers: false) is not { } filtered) return null;
        if (filtered.Verdict == ContextVerdict.Rejected)
        {
            run.ContextNote = ContextNote.Skipped;
            return null;
        }
        var context = captured with { Text = filtered.Text };
        run.ContextSource = context.Source;
        run.ContextChars = context.Text.Length;
        run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Context = context.Text, ContextSource = context.Source }));
        return context;
    }

    /// <summary>
    /// The rewritten text, or null when the rewriter is missing or failed (the caller falls back). Time limits: the
    /// mode's first-token wait, then <see cref="RewriteGap"/> between deltas, and <see cref="RewriteTotal"/> overall.
    /// Running out is a Timeout fallback, never an interrupted run.
    /// </summary>
    private async Task<string?> RewriteAsync(Run run, ModeDefinition def, string raw, CapturedContext? context, Profile profile, CancellationToken ct)
    {
        if (rewriter is null)
        {
            run.RewriteError = RewriteFailure.Unavailable;
            return null;
        }
        var text = new StringBuilder();
        using var total = new CancellationTokenSource(RewriteTotal, time);
        using var stall = new CancellationTokenSource(def.FirstTokenTimeout, time);
        using var limits = CancellationTokenSource.CreateLinkedTokenSource(ct, total.Token, stall.Token);
        try
        {
            await foreach (var delta in rewriter.StreamAsync(new RewriteRequest(run.Mode, raw, context, profile), limits.Token))
            {
                if (text.Length == 0) run.Timing = run.Timing with { RewriteFirstTokenMs = run.SinceAudioEnd() };
                stall.CancelAfter(RewriteGap);
                text.Append(delta);
                run.Report(new RewriteDelta(delta));
            }
            run.Timing = run.Timing with { RewriteDoneMs = run.SinceAudioEnd() };
            if (OutputText.Normalise(text.ToString()).Length == 0) return Failed(RewriteFailure.EmptyOutput);
            return text.ToString();
        }
        catch (RewriteException ex)
        {
            return Failed(ex.Failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return Failed(RewriteFailure.Timeout);
        }

        string? Failed(RewriteFailure failure)
        {
            run.RewriteError = failure;
            log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Rewrite), LogField.Tag("rewriteError", failure));
            return null;
        }
    }

    private RunResult SttFailed(Run run, SttError error, StageStatus status)
    {
        run.SttError = error;
        run.Done(Stage.Stt, status);
        run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = RunOutcome.SttFailed, SttError = error, Stages = run.Stages }));
        return run.Finish(RunOutcome.SttFailed);
    }

    private RunResult Cancelled(Run run, Stage stage)
    {
        run.Done(stage, StageStatus.Cancelled);
        if (run.Previous is { } previous)
        {
            // A cancelled retry puts the row back as it was; its audio stays for another try.
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = previous.Outcome, Mode = previous.Mode }));
        }
        else
        {
            // Cancelled after the recording ended (a tap while transcribing or delivering): the row and the audio stay,
            // so an accidental tap never loses a dictation (Bryan, 2026-10-28; the spec said delete). A discard while
            // still recording is handled in RunCoreAsync and deletes everything.
            run.TryHistory(() => history.Update(run.HistoryId!.Value, r => r with { Outcome = RunOutcome.Cancelled, Stages = run.Stages }));
        }
        return run.Finish(RunOutcome.Cancelled);
    }

    private RunResult? Ready(Run run)
    {
        var readiness = stt.CheckReady();
        if (readiness == SttReadiness.Ready) return null;
        run.SttError = readiness switch
        {
            SttReadiness.MissingKey => SttError.MissingKey,
            SttReadiness.InsecureEndpoint => SttError.InsecureEndpoint,
            _ => SttError.InvalidEndpoint,
        };
        run.Done(Stage.Stt, StageStatus.Failed);
        return run.Finish(RunOutcome.SttFailed);
    }

    /// <summary>
    /// Clear history: rows, pending recordings and the WAL, plus the in-memory last result. Waits for a run in
    /// progress to finish and holds the run gate meanwhile, so no run can write a row back mid-clear (a hold then
    /// gets Busy). False if history couldn't be written.
    /// </summary>
    public async Task<bool> ClearHistoryAsync(Output.LastResult? last, CancellationToken ct)
    {
        while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) await Task.Delay(50, ct);
        try
        {
            last?.Clear();
            history.Clear();
            return true;
        }
        catch (HistoryUnavailableException)
        {
            return false;
        }
        finally
        {
            Exit();
        }
    }

    private bool TryEnter(Mode mode, out RunResult busy)
    {
        busy = null!;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
        {
            // Set at once, so a hold rejected during this run's first moments can still say what is running.
            _current = new CurrentRun(Guid.Empty, mode, Stage.Capture, time.GetUtcNow());
            return true;
        }
        log.Write(LogEvent.RunRejectedBusy);
        var current = _current;
        busy = new RunResult { RunId = Guid.Empty, Outcome = RunOutcome.Busy, Mode = current?.Mode ?? Mode.Raw, BusyWith = current };
        return false;
    }

    private void Exit()
    {
        _current = null;
        Volatile.Write(ref _busy, 0);
    }

    /// <summary>The mutable state of one run, and the bookkeeping every exit path shares.</summary>
    private sealed class Run
    {
        private readonly DictationPipeline _p;
        private readonly IProgress<PipelineEvent>? _progress;
        private readonly Dictionary<Stage, StageStatus> _stages = [];
        private readonly Dictionary<Stage, long> _stageMs = [];
        private long _stageStart;
        private long _audioEnd;

        public Run(DictationPipeline pipeline, Guid id, Mode mode, IProgress<PipelineEvent>? progress)
        {
            _p = pipeline;
            _progress = progress;
            Id = id;
            Mode = mode;
            StartedUtc = pipeline.Time.GetUtcNow();
            StartTimestamp = pipeline.Time.GetTimestamp();
            _stageStart = StartTimestamp;
            Timing = new RunTiming { RunId = id, Mode = mode, StartedUtc = StartedUtc, Outcome = RunOutcome.InProgress, Provider = pipeline.Stt.Provider };
            pipeline._current = new CurrentRun(id, mode, Stage.Capture, StartedUtc);
        }

        public Guid Id { get; }
        public Mode Mode { get; set; }
        public DateTimeOffset StartedUtc { get; }
        public long StartTimestamp { get; }
        public long? HistoryId { get; set; }
        public bool HistoryWritten { get; set; }
        public string? WavName { get; set; }

        /// <summary>Read once at the start: pausing mid-run applies from the next run.</summary>
        public bool HistoryPaused { get; init; }

        /// <summary>The hold's mic timing, read at the finish; null for retries and re-runs.</summary>
        public Func<MicTiming>? Mic { get; init; }

        /// <summary>A re-run from History: copied, never pasted or previewed, since there's no target to paste into.</summary>
        public bool ClipboardOnly { get; init; }
        public PendingRecording? Recording { get; set; }
        public HistoryRow? Previous { get; init; }
        public long AudioBytes { get; set; }
        public long CaptureMs { get; set; }
        public string? RawText { get; set; }
        public string? Output { get; set; }
        public DeliveryKind Delivery { get; set; }
        public bool FallbackUsed { get; set; }
        public SttError? SttError { get; set; }
        public CaptureError? CaptureError { get; set; }
        public RewriteFailure? RewriteError { get; set; }
        public ContextNote ContextNote { get; set; }
        public bool Rewritten { get; set; }
        public ContextSource? ContextSource { get; set; }
        public int ContextChars { get; set; }
        public RunTiming Timing { get; set; }
        public IReadOnlyDictionary<Stage, StageStatus> Stages => new Dictionary<Stage, StageStatus>(_stages);
        public IReadOnlyDictionary<Stage, long> StageMs => new Dictionary<Stage, long>(_stageMs);

        public void Report(PipelineEvent e) => _progress?.Report(e);

        public void MarkAudioEnd() => _audioEnd = _p.Time.GetTimestamp();

        public long SinceAudioEnd() => (long)_p.Time.GetElapsedTime(_audioEnd).TotalMilliseconds;

        public void SetStage(Stage stage)
        {
            _stageStart = _p.Time.GetTimestamp();
            _p._current = new CurrentRun(Id, Mode, stage, StartedUtc);
        }

        public void Done(Stage stage, StageStatus status)
        {
            _stages[stage] = status;
            _stageMs[stage] = (long)_p.Time.GetElapsedTime(_stageStart).TotalMilliseconds;
            Report(new StageFinished(stage, status));
            if (status is StageStatus.Ok or StageStatus.Skipped) _p.Log.Write(LogEvent.StageCompleted, LogField.Tag("stage", stage), LogField.Ms("ms", _stageMs[stage]));
        }

        /// <summary>Time inside history writes so far: summed as a span, so sub-millisecond WAL writes still add up.</summary>
        private TimeSpan _historyTime;
        private TimeSpan? _historyBeforePaste;

        /// <summary>The paste is about to start: what history has cost by now is what it added before it.</summary>
        public void MarkPasteStart() => _historyBeforePaste = _historyTime;

        /// <summary>Runs a history write; a failure marks history unavailable and the run carries on.</summary>
        public T? TryHistory<T>(Func<T> write) where T : struct
        {
            if (!HistoryWritten && HistoryId is not null) return null;
            // Timed here only (the Action overload comes through here), and a failed write still counts.
            var start = _p.Time.GetTimestamp();
            try
            {
                return write();
            }
            catch (HistoryUnavailableException)
            {
                HistoryWritten = false;
                _p.Log.Write(LogEvent.HistoryWriteFailed);
                return null;
            }
            finally
            {
                _historyTime += _p.Time.GetElapsedTime(start);
            }
        }

        public void TryHistory(Action write)
        {
            if (HistoryId is null) return;
            TryHistory(() =>
            {
                write();
                return 0;
            });
        }

        /// <param name="flushWal">
        /// The row held text that mustn't wait in the WAL for a later checkpoint (a password field's run). Done here,
        /// while there's still a row: a checkpoint after this, through TryHistory, would be skipped, since the cleared
        /// HistoryId makes TryHistory a no-op. Off for a tap or a failed capture, whose row has no text yet: those
        /// would pay a checkpoint's fsync for nothing.
        /// </param>
        public void DeleteRow(bool flushWal = false)
        {
            if (HistoryId is not { } id) return;
            try
            {
                _p.History.Delete(id);
            }
            catch (HistoryUnavailableException)
            {
                _p.Log.Write(LogEvent.HistoryDeleteFailed);
            }
            if (flushWal)
            {
                // A failed or busy checkpoint is logged by the store and the run carries on; the blanked text then waits in the
                // WAL for the next truncating checkpoint (a delete, a prune, a clear or another refused run).
                try { _p.History.Checkpoint(); }
                catch (HistoryUnavailableException) { }
            }
            HistoryId = null;
            HistoryWritten = false;
        }

        public void DeleteWav()
        {
            if (WavName is null) return;
            _p.Pending.Delete(WavName);
            TryHistory(() => _p.History.Update(HistoryId!.Value, r => r with { PendingAudioFile = null }));
            WavName = null;
        }

        public RunResult Finish(RunOutcome outcome)
        {
            var mic = Mic?.Invoke();
            Timing = Timing with
            {
                MicFirstAudioMs = mic?.FirstAudioMs,
                MicKind = mic?.Kind,
                MicStart = mic?.Start,
                HistoryWriteMs = (long)_historyTime.TotalMilliseconds,
                // Paused: there's no history to cost anything, and a 0 would read as "fast".
                HistoryBeforePasteMs = !HistoryPaused && _historyBeforePaste is { } before ? (long)before.TotalMilliseconds : null,
                Mode = Mode,
                Outcome = outcome,
                CaptureMs = CaptureMs,
                AudioBytes = AudioBytes,
                Stages = Stages,
                TranscriptChars = RawText?.Length ?? 0,
                OutputChars = Output?.Length ?? 0,
                // Whitespace tokens: undercounts CJK text, which has no spaces. Fine for the Done card's rough number.
                OutputWords = Output?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length ?? 0,
            };
            _p.Timings.Append(Timing);
            _p.Log.Write(LogEvent.RunFinished, LogField.Tag("outcome", outcome), LogField.Tag("mode", Mode),
                LogField.Count("chars", Timing.OutputChars), LogField.Ms("totalMs", (long)_p.Time.GetElapsedTime(StartTimestamp).TotalMilliseconds));
            return new RunResult
            {
                RunId = Id,
                Outcome = outcome,
                Mode = Mode,
                RawText = RawText,
                Output = Output,
                Delivery = Delivery,
                HistoryId = HistoryId,
                HistoryWritten = HistoryWritten && HistoryId is not null,
                SttError = SttError,
                CaptureError = CaptureError,
                RewriteError = RewriteError,
                ContextNote = ContextNote,
                Rewritten = Rewritten,
                ContextSource = ContextSource,
                ContextChars = ContextChars,
                Timing = Timing,
                HistoryPaused = HistoryPaused,
            };
        }
    }
}
