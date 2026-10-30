using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Logging;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Pipeline;

public sealed class DictationPipelineTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Raw_HappyPath_EventOrder_RowBeforeDeliver_OutputBeforeDeliver_WavDeleted()
    {
        HistoryRow? seenAtDelivery = null;
        long? rowId = null;
        _rig.Sink.OnDeliver = _ => seenAtDelivery = _rig.History.Recent(1).SingleOrDefault();
        var progress = new ListProgress();

        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), progress: progress), CancellationToken.None);
        rowId = result.HistoryId;

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal("hello world", result.Output);
        Assert.Equal("hello world", result.RawText);
        Assert.Equal(DeliveryKind.Pasted, result.Delivery);
        Assert.True(result.HistoryWritten);

        // The row existed, with the output in it, before the sink was called.
        Assert.NotNull(seenAtDelivery);
        Assert.Equal("hello world", seenAtDelivery!.Output);
        Assert.Equal(DeliveryIntent.Paste, _rig.Sink.Delivered.Single().Intent);

        var row = _rig.History.Get(rowId!.Value)!;
        Assert.Equal(RunOutcome.Completed, row.Outcome);
        Assert.Equal(DeliveryKind.Pasted, row.Delivery);
        Assert.Null(row.PendingAudioFile);
        Assert.Equal("slack", row.ProcessName);
        Assert.Empty(_rig.Pending.List());

        var kinds = progress.Events.Where(e => e is not CaptureLevel).Select(e => e.ToString()).ToList();
        Assert.Equal(
        [
            new StageStarted(Stage.Capture).ToString(),
            new StageFinished(Stage.Capture, StageStatus.Ok).ToString(),
            new StageStarted(Stage.Stt).ToString(),
            new TranscriptReady("hello world").ToString(),
            new StageFinished(Stage.Stt, StageStatus.Ok).ToString(),
            new StageFinished(Stage.Rewrite, StageStatus.Skipped).ToString(),
            new StageStarted(Stage.Output).ToString(),
            new StageFinished(Stage.Output, StageStatus.Ok).ToString(),
        ], kinds);
        Assert.Contains(progress.Events, e => e is CaptureLevel);
    }

    [Fact]
    public async Task Silence_NoSpeech_NoSttCall_WavAndRowKept_Retryable()
    {
        var result = await _rig.Build().RunAsync(PipelineRig.Request(new FileAudioSource(Fixtures.Audio("noise-floor-5s.wav"))), CancellationToken.None);

        Assert.Equal(RunOutcome.NoSpeech, result.Outcome);
        Assert.Equal(0, _rig.Stt.Completions);
        Assert.Equal(1, _rig.Stt.DisposedUncompleted); // a streamed session would be torn down, its body never ended
        Assert.Single(_rig.Pending.List());
        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal(RunOutcome.NoSpeech, row.Outcome);
        Assert.Equal(row.Id, _rig.History.LatestRetryable()!.Id);
        Assert.Empty(_rig.Sink.Delivered);
    }

    [Fact]
    public async Task EmptyTranscript_IsNoSpeech_WavKept_NothingDelivered()
    {
        _rig.Stt.Text = "  ";
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.NoSpeech, result.Outcome);
        Assert.Empty(_rig.Sink.Delivered);
        Assert.Single(_rig.Pending.List());
        Assert.Equal(result.HistoryId, _rig.History.LatestRetryable()!.Id);
    }

    [Fact]
    public async Task UnexpectedException_Interrupted_RowRetryable_GateReleased_TimingWritten()
    {
        var pipeline = _rig.Build(stt: new ThrowingFactory(_rig.Stt));
        var result = await pipeline.RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.Interrupted, result.Outcome);
        Assert.Equal(RunOutcome.Interrupted, _rig.History.Get(result.HistoryId!.Value)!.Outcome);
        Assert.Equal(result.HistoryId, _rig.History.LatestRetryable()!.Id);
        Assert.Single(_rig.Timings);
        Assert.Null(pipeline.Current);
        Assert.Equal(RunOutcome.Completed, (await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None)).Outcome);
    }

    /// <summary>Fails the way a bug would: an exception type the pipeline doesn't expect, after capture.</summary>
    private sealed class ThrowingFactory(FakeStt inner) : ITranscriptionSessionFactory
    {
        public SttProviderKind Provider => inner.Provider;
        public SttReadiness CheckReady() => SttReadiness.Ready;
        public ITranscriptionSession Create(SttSessionOptions options) => new Session();

        private sealed class Session : ITranscriptionSession
        {
            public void PushAudio(ReadOnlyMemory<byte> pcm) { }
            public Task<SttResult> CompleteAsync(CancellationToken ct) => throw new NullReferenceException("bug");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Busy_DuringStartup_StillReportsMode()
    {
        _rig.Stt.WaitForCancel = true;
        _rig.Stt.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = _rig.Build();
        using var cts = new CancellationTokenSource();
        var first = pipeline.RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Reply), cts.Token);
        await _rig.Stt.Entered.Task;
        var busy = await pipeline.RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        Assert.Equal(Mode.Reply, busy.Mode);
        cts.Cancel();
        await first;
    }

    [Fact]
    public async Task QuietSpeechFixture_NotNoSpeech()
    {
        var result = await _rig.Build().RunAsync(PipelineRig.Request(new FileAudioSource(Fixtures.Audio("speech-quiet.wav"))), CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task SecondRunWhileBusy_BusyWithCurrent_NoRowNoTiming_FirstUnaffected()
    {
        _rig.Stt.WaitForCancel = true;
        _rig.Stt.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = _rig.Build();
        using var cts = new CancellationTokenSource();
        var first = pipeline.RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Dictate), cts.Token);
        await _rig.Stt.Entered.Task;

        var second = await pipeline.RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.Busy, second.Outcome);
        Assert.NotNull(second.BusyWith);
        Assert.Equal(Mode.Dictate, second.BusyWith!.Mode);
        Assert.Equal(Stage.Stt, second.BusyWith.Stage);
        Assert.Null(second.Timing);
        Assert.Contains(LogEvent.RunRejectedBusy, _rig.Log.Events);
        Assert.Single(_rig.History.Recent(10));

        cts.Cancel();
        Assert.Equal(RunOutcome.Cancelled, (await first).Outcome);
        Assert.Single(_rig.Timings);
        Assert.Null(pipeline.Current);
    }

    [Fact]
    public async Task SourceThrowsAccessDenied_CaptureFailed_GateReleased_NothingKept()
    {
        var pipeline = _rig.Build();
        var result = await pipeline.RunAsync(PipelineRig.Request(new ThrowingSource(PipelineRig.Speech(), 0, CaptureError.AccessDenied)), CancellationToken.None);

        Assert.Equal(RunOutcome.CaptureFailed, result.Outcome);
        Assert.Equal(CaptureError.AccessDenied, result.CaptureError);
        Assert.Empty(_rig.Pending.List());
        Assert.Empty(_rig.History.Recent(10));
        Assert.Null(pipeline.Current);
        Assert.Equal(RunOutcome.Completed, (await pipeline.RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task DeviceLostMidRecording_CaptureFailed_PartialWavAndRowKept()
    {
        var result = await _rig.Build().RunAsync(PipelineRig.Request(new ThrowingSource(PipelineRig.Speech(), 5, CaptureError.DeviceLost)), CancellationToken.None);

        Assert.Equal(RunOutcome.CaptureFailed, result.Outcome);
        var wav = Assert.Single(_rig.Pending.List());
        Assert.Equal(5 * 3200, WavFile.ReadPcm(_rig.Pending.PathOf(wav)).Length);
        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal(CaptureError.DeviceLost, row.CaptureError);
        Assert.Equal(wav, row.PendingAudioFile);
    }

    [Fact]
    public async Task CancelDuringStt_KeepsRecordingForRetry()
    {
        _rig.Stt.WaitForCancel = true;
        _rig.Stt.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var run = _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), cts.Token);
        await _rig.Stt.Entered.Task;
        cts.Cancel();

        // Bryan, 2026-10-28: a cancel tap after the recording ended keeps it, so an accidental tap loses nothing.
        var result = await run;
        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.Single(_rig.Pending.List());
        Assert.Equal(result.HistoryId, _rig.History.LatestRetryable()!.Id);
    }

    [Fact]
    public async Task CancelDuringCapture_StillDiscardsEverything()
    {
        using var cts = new CancellationTokenSource();
        // Still recording: the hold hasn't been committed, so the intent hasn't resolved.
        var intent = new TaskCompletionSource<RunIntent>();
        var request = new RunRequest(new CancellingSource(cts), Mode.Raw, "slack", intent.Task);
        var result = await _rig.Build().RunAsync(request, cts.Token);
        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.Empty(_rig.History.Recent(10));
        Assert.Empty(_rig.Pending.List());
        Assert.Equal(1, _rig.Stt.DisposedUncompleted);
    }

    [Fact]
    public async Task AHold_CreatesALiveSession_ButRetry_SendsAFinishedBuffer()
    {
        // Only a hold's audio can be streamed; Retry pushes the whole file at once, so it always goes as one request.
        var hold = await _rig.Build().RunAsync(PipelineRig.Request(new FileAudioSource(Fixtures.Audio("noise-floor-5s.wav"))), CancellationToken.None);
        Assert.True(_rig.Stt.Created.Single().Live);
        await _rig.Build().RetryAsync(hold.HistoryId!.Value, null, null, CancellationToken.None);
        Assert.False(_rig.Stt.Created.Last().Live);
    }

    [Fact]
    public async Task HowTheAudioWasSent_ReachesTheTimingLog()
    {
        _rig.Stt.Upload = SttUpload.Streamed;
        await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        Assert.Equal(SttUpload.Streamed, _rig.Timings.Last().SttUpload);
    }

    /// <summary>Yields one chunk, then cancels: a user pressing Esc mid-recording.</summary>
    private sealed class CancellingSource(CancellationTokenSource cts) : IAudioSource
    {
        public async IAsyncEnumerable<AudioChunk> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AudioChunk(new byte[3200], 0.2f);
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [Fact]
    public async Task CancelAfterStt_RowKeepsRaw_Cancelled()
    {
        using var cts = new CancellationTokenSource();
        _rig.Rewriter = new FakeRewriter(_ =>
        {
            cts.Cancel();
            return ["never"];
        });
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Prompt), cts.Token);

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal("hello world", row.RawText);
        Assert.Equal(RunOutcome.Cancelled, row.Outcome);
        Assert.Empty(_rig.Sink.Delivered);
        // Bryan, 2026-10-28: a cancel after the recording ended keeps the audio for Retry, even after STT.
        Assert.Single(_rig.Pending.List());
        Assert.Equal(row.Id, _rig.History.LatestRetryable()!.Id);
    }

    [Fact]
    public async Task CancelAfterCommit_WhileAudioStillDraining_KeepsAllOfIt()
    {
        using var cts = new CancellationTokenSource();
        var request = new RunRequest(new CancelMidDrainSource(cts), Mode.Raw, "slack", Task.FromResult(new RunIntent(Mode.Raw, false)));
        var result = await _rig.Build().RunAsync(request, cts.Token);

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        var wav = Assert.Single(_rig.Pending.List());
        Assert.Equal(5 * 3200, WavFile.ReadPcm(_rig.Pending.PathOf(wav)).Length);
        Assert.Equal(0, _rig.Stt.Completions);
        Assert.Equal(result.HistoryId, _rig.History.LatestRetryable()!.Id);
    }

    [Fact]
    public async Task CancelAfterCommit_WithACaptureThatNeverEnds_GivesUpAfterTheDrainLimit()
    {
        using var cts = new CancellationTokenSource();
        var source = new StuckAfterCancelSource(cts);
        var request = new RunRequest(source, Mode.Raw, "slack", Task.FromResult(new RunIntent(Mode.Raw, false)));
        var run = _rig.Build().RunAsync(request, cts.Token);
        await source.Stuck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The drain waits out the whole limit before giving up.
        _rig.Time.Advance(DictationPipeline.DrainLimit - TimeSpan.FromMilliseconds(1));
        await Task.Delay(200);
        Assert.False(run.IsCompleted);

        _rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        var wav = Assert.Single(_rig.Pending.List());
        Assert.Equal(2 * 3200, WavFile.ReadPcm(_rig.Pending.PathOf(wav)).Length);
    }

    /// <summary>A device that never reports "stopped": after the cancel tap, the read waits on the token alone.</summary>
    private sealed class StuckAfterCancelSource(CancellationTokenSource cts) : IAudioSource
    {
        public TaskCompletionSource Stuck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<AudioChunk> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AudioChunk(new byte[3200], 0.2f);
            yield return new AudioChunk(new byte[3200], 0.2f);
            cts.Cancel();
            Stuck.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    /// <summary>The hold was committed (intent resolved); the user taps cancel while buffered audio is still being read.</summary>
    private sealed class CancelMidDrainSource(CancellationTokenSource cts) : IAudioSource
    {
        public async IAsyncEnumerable<AudioChunk> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < 5; i++)
            {
                if (i == 2) cts.Cancel();
                await Task.Yield();
                yield return new AudioChunk(new byte[3200], 0.2f);
            }
        }
    }

    [Theory]
    [InlineData(SttError.Network)]
    [InlineData(SttError.Auth)]
    public async Task SttFailure_WavKept_RowHasErrorAndFile(SttError error)
    {
        _rig.Stt.Throw = new SttException(error, error == SttError.Auth ? 401 : null);
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.SttFailed, result.Outcome);
        Assert.Equal(error, result.SttError);
        var wav = Assert.Single(_rig.Pending.List());
        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal(error, row.SttError);
        Assert.Equal(wav, row.PendingAudioFile);
        Assert.Equal(RunOutcome.SttFailed, row.Outcome);
    }

    [Fact]
    public void SttTimeout_ScalesWithAudio()
    {
        Assert.Equal(TimeSpan.FromSeconds(15) + TimeSpan.FromSeconds(0.9), SttTimeout.For(3 * 32000));
        Assert.Equal(TimeSpan.FromSeconds(105), SttTimeout.For(300 * 32000));
    }

    [Fact]
    public async Task SttTimeout_IsTimeoutNotCancelled()
    {
        _rig.Stt.WaitForCancel = true;
        _rig.Stt.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        await _rig.Stt.Entered.Task;
        _rig.Time.Advance(TimeSpan.FromSeconds(30));

        var result = await run;
        Assert.Equal(RunOutcome.SttFailed, result.Outcome);
        Assert.Equal(SttError.Timeout, result.SttError);
        Assert.Single(_rig.Pending.List());
    }

    [Fact]
    public async Task SttNotReady_FailsBeforeRecording_NothingKept()
    {
        _rig.Stt.Readiness = SttReadiness.MissingKey;
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.SttFailed, result.Outcome);
        Assert.Equal(SttError.MissingKey, result.SttError);
        Assert.Empty(_rig.Pending.List());
        Assert.Empty(_rig.History.Recent(10));
    }

    [Fact]
    public async Task Dictate_NoRewriter_PastesRaw_FellBack()
    {
        _rig.Stt.Text = PipelineRig.LongTranscript; // long enough that the short-dictation gate doesn't skip the LLM
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Dictate), CancellationToken.None);

        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
        Assert.Equal(RewriteFailure.Unavailable, result.RewriteError);
        Assert.Equal(DeliveryIntent.Paste, _rig.Sink.Delivered.Single().Intent);
        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.True(row.FallbackUsed);
        Assert.Equal(StageStatus.FellBack, row.Stages[Stage.Rewrite]);
    }

    [Fact]
    public async Task Prompt_NoRewriter_ClipboardOnlyRaw_FellBack()
    {
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Prompt), CancellationToken.None);

        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
        var item = _rig.Sink.Delivered.Single();
        Assert.Equal(DeliveryIntent.ClipboardOnly, item.Intent);
        Assert.Equal("hello world", item.Text);
    }

    [Fact]
    public async Task Rewriter_StreamsDeltas_Concatenates()
    {
        _rig.Rewriter = new FakeRewriter(r => [$"[{r.Mode}] ", "Hello", " world."]);
        var progress = new ListProgress();
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Prompt, "ctx", progress), CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        // Prompt mode: the model's text, then the selection attached verbatim by code.
        Assert.Equal("[Prompt] Hello world.\n\nSelected text:\n```\nctx\n```", result.Output);
        Assert.Equal(3, progress.Events.OfType<RewriteDelta>().Count());
        Assert.Equal("ctx", _rig.History.Get(result.HistoryId!.Value)!.Context);
    }

    [Fact]
    public async Task RewriteExceptionAfterPartial_FallsBack_ErrorRecorded()
    {
        _rig.Rewriter = new FakeRewriter(_ => ["partial"], RewriteFailure.MaxTokens);
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Reply), CancellationToken.None);

        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
        Assert.Equal(RewriteFailure.MaxTokens, result.RewriteError);
        Assert.Equal("hello world", result.Output);
        Assert.Equal("MaxTokens", _rig.History.Get(result.HistoryId!.Value)!.RewriteError);
    }

    [Fact]
    public async Task ModeChangedDuringHold_RowAndRewriteUseFinalMode()
    {
        _rig.Rewriter = new FakeRewriter(r => [r.Mode.ToString()]);
        var request = new RunRequest(PipelineRig.Speech(), Mode.Dictate, "code", Task.FromResult(new RunIntent(Mode.Prompt, false)));
        var result = await _rig.Build().RunAsync(request, CancellationToken.None);

        Assert.Equal(Mode.Prompt, result.Mode);
        Assert.Equal("Prompt", result.Output);
        Assert.Equal(Mode.Prompt, _rig.History.Get(result.HistoryId!.Value)!.Mode);
    }

    [Fact]
    public async Task Delivery_Refused_DeletesRow()
    {
        _rig.Sink.Result = DeliveryKind.Refused;
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.Discarded, result.Outcome);
        Assert.Equal(DeliveryKind.Refused, result.Delivery);
        Assert.Empty(_rig.History.Recent(10));
        Assert.Empty(_rig.Pending.List());
    }

    [Fact]
    public async Task Delivery_Refused_LeavesNoTextInTheWal()
    {
        // A password field's run: its row is blanked and deleted, and the WAL must be flushed so no copy of the text
        // waits there for a later checkpoint.
        _rig.Sink.Result = DeliveryKind.Refused;
        await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        // The text searched for is the one this run really dictated, or the search would prove nothing.
        Assert.Equal("hello world", Assert.Single(_rig.Sink.Delivered).Text);
        var wal = _rig.Root.Paths.HistoryDb + "-wal";
        Assert.True(File.Exists(wal), "no WAL file: the test would prove nothing");
        foreach (var file in new[] { wal, _rig.Root.Paths.HistoryDb })
        {
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            f.CopyTo(bytes);
            Assert.DoesNotContain("hello world", System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
        }
    }

    [Fact]
    public async Task Delivery_Discarded_Outcome()
    {
        _rig.Sink.Result = DeliveryKind.Discarded;
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.Discarded, result.Outcome);
        Assert.Equal(RunOutcome.Discarded, _rig.History.Get(result.HistoryId!.Value)!.Outcome);
    }

    [Fact]
    public async Task DeliverThrows_OutputFailed_RowKeepsOutput()
    {
        _rig.Sink.Throw = new InvalidOperationException("clipboard locked");
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.OutputFailed, result.Outcome);
        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal("hello world", row.Output);
        Assert.Equal(RunOutcome.OutputFailed, row.Outcome);
        // Not retryable, and the row holds the output: the audio is no longer needed.
        Assert.Empty(_rig.Pending.List());
    }

    [Fact]
    public async Task HistoryUnavailable_StillDelivers_HistoryWrittenFalse_WavDeletedAfterDelivery()
    {
        var result = await _rig.Build(history: new UnavailableHistory()).RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.False(result.HistoryWritten);
        Assert.Null(result.HistoryId);
        Assert.Single(_rig.Sink.Delivered);
        Assert.Empty(_rig.Pending.List());
    }

    [Fact]
    public async Task HistoryUnavailable_DeliveryFails_WavKept()
    {
        _rig.Sink.Throw = new InvalidOperationException("boom");
        var result = await _rig.Build(history: new UnavailableHistory()).RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);

        Assert.Equal(RunOutcome.OutputFailed, result.Outcome);
        Assert.Single(_rig.Pending.List());
    }

    [Fact]
    public async Task Timing_MilestonesRelativeToAudioEnd_AndStageStatuses()
    {
        _rig.Rewriter = new FakeRewriter(_ => ["Hi there friend"]);
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Prompt), CancellationToken.None);

        var t = Assert.Single(_rig.Timings);
        Assert.Same(result.Timing, t);
        Assert.Equal(RunOutcome.Completed, t.Outcome);
        Assert.Equal(Mode.Prompt, t.Mode);
        Assert.True(t.AudioBytes > 0);
        Assert.NotNull(t.SttDoneMs);
        Assert.NotNull(t.RewriteFirstTokenMs);
        Assert.NotNull(t.OutputDoneMs);
        Assert.Equal(StageStatus.Ok, t.Stages[Stage.Rewrite]);
        Assert.Equal(11, t.TranscriptChars);
        Assert.Equal(3, t.OutputWords);
    }
}
