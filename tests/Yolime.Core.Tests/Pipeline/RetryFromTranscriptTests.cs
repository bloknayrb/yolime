using System.Runtime.CompilerServices;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Pipeline;

public sealed class RetryFromTranscriptTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>A Prompt run whose rewrite failed: the transcript is in history, the audio is gone.</summary>
    private async Task<long> FailedPrompt(Mode mode = Mode.Prompt)
    {
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), mode), CancellationToken.None);
        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
        Assert.Empty(_rig.Pending.List());
        return result.HistoryId!.Value;
    }

    [Fact]
    public async Task AFailedPromptRewrite_IsRetryable_FromItsTranscript()
    {
        var id = await FailedPrompt();
        var row = _rig.History.LatestRetryable()!;
        Assert.Equal(id, row.Id);
        Assert.Null(row.PendingAudioFile);
        Assert.NotNull(row.RawText);
    }

    [Fact]
    public async Task ADictateFallback_IsNotRetryable_ItWasPasted()
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        await FailedPrompt(Mode.Dictate);
        Assert.Null(_rig.History.LatestRetryable());
    }

    [Fact]
    public async Task Success_RewritesWithoutStt_ClipboardOnly_AndClearsTheError()
    {
        var id = await FailedPrompt();
        var fake = new FakeRewriter(_ => ["Better prompt."]);
        _rig.Rewriter = fake;
        var completionsBefore = _rig.Stt.Completions;
        var sinkBefore = _rig.Sink.Delivered.Count;

        var result = await _rig.Build().RunFromTranscriptAsync(id, null, CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(completionsBefore, _rig.Stt.Completions);
        Assert.Equal("hello world", Assert.Single(fake.Requests).Transcript);
        Assert.Equal("Better prompt.", _rig.Sink.Delivered[sinkBefore].Text);
        var row = _rig.History.Get(id)!;
        Assert.Equal(RunOutcome.Completed, row.Outcome);
        Assert.Null(row.RewriteError);
        Assert.Equal("Better prompt.", row.Output);
        Assert.Null(_rig.History.LatestRetryable());
    }

    [Fact]
    public async Task NotReady_TouchesNothing()
    {
        var id = await FailedPrompt();
        _rig.Rewriter = new FakeRewriter(_ => ["x"]) { Readiness = RewriterReadiness.MissingKey };
        var result = await _rig.Build().RunFromTranscriptAsync(id, null, CancellationToken.None);

        Assert.Equal(RunOutcome.RewriteFailed, result.Outcome);
        Assert.Equal(RewriteFailure.MissingKey, result.RewriteError);
        Assert.Equal(RunOutcome.CompletedWithFallback, _rig.History.Get(id)!.Outcome);
    }

    [Fact]
    public async Task Cancel_PutsTheRowBack()
    {
        var id = await FailedPrompt();
        using var cts = new CancellationTokenSource();
        var hang = new HangingRewriter();
        _rig.Rewriter = hang;
        var run = _rig.Build().RunFromTranscriptAsync(id, null, cts.Token);
        await hang.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        Assert.Equal(RunOutcome.Cancelled, (await run).Outcome);
        var row = _rig.History.Get(id)!;
        Assert.Equal(RunOutcome.CompletedWithFallback, row.Outcome);
        Assert.Equal(id, _rig.History.LatestRetryable()!.Id);
    }

    [Fact]
    public async Task WhileAnotherRunIsBusy_ItIsRejected()
    {
        var id = await FailedPrompt();
        var hang = new HangingRewriter();
        _rig.Rewriter = hang;
        var pipeline = _rig.Build();
        using var cts = new CancellationTokenSource();
        var first = pipeline.RunFromTranscriptAsync(id, null, cts.Token);
        await hang.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunOutcome.Busy, (await pipeline.RunFromTranscriptAsync(id, null, CancellationToken.None)).Outcome);
        cts.Cancel();
        await first;
    }

    [Fact]
    public async Task TheNewestCandidate_Wins_AudioOrTranscript()
    {
        var transcriptRow = await FailedPrompt();
        _rig.Time.Advance(TimeSpan.FromSeconds(5));
        _rig.Stt.Throw = new Yolime.Core.Stt.SttException(Yolime.Core.Stt.SttError.Network);
        var audioRow = (await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech(), Mode.Prompt), CancellationToken.None)).HistoryId!.Value;

        Assert.Equal(audioRow, _rig.History.LatestRetryable()!.Id);
        Assert.NotEqual(transcriptRow, audioRow);
    }

    private sealed class HangingRewriter : IRewriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RewriterReadiness CheckReady() => RewriterReadiness.Ready;

        public async IAsyncEnumerable<string> StreamAsync(RewriteRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }
}
