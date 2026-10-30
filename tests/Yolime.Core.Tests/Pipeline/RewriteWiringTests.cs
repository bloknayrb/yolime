using System.Runtime.CompilerServices;
using Yolime.Core.Llm;
using Yolime.Core.Modes;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Pipeline;

public sealed class RewriteWiringTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static RunRequest Request(Mode mode, Task<CapturedContext?>? context = null, Profile profile = Profile.Neutral) =>
        new(PipelineRig.Speech(), mode, "slack", Task.FromResult(new RunIntent(mode, false)), Context: context, Profile: profile);

    private static Task<CapturedContext?> Ctx(string text, ContextSource source = ContextSource.Selection) =>
        Task.FromResult<CapturedContext?>(new CapturedContext(text, source, 0));

    /// <summary>Yields the given deltas, then waits on the token until the pipeline's limits cancel it.</summary>
    private sealed class StallingRewriter(params string[] before) : IRewriter
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RewriterReadiness CheckReady() => RewriterReadiness.Ready;

        public async IAsyncEnumerable<string> StreamAsync(RewriteRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var d in before) yield return d;
            Waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    [Fact]
    public async Task ShortDictate_SkipsTheLlm_WithLocalCleanup()
    {
        var fake = new FakeRewriter(_ => ["never"]);
        _rig.Rewriter = fake;
        var result = await _rig.Build().RunAsync(Request(Mode.Dictate), CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal("Hello world.", result.Output);
        Assert.Empty(fake.Requests);
        Assert.False(result.Rewritten); // local cleanup isn't a rewrite worth undoing
        Assert.Equal(StageStatus.Skipped, result.Timing!.Stages[Stage.Rewrite]);
    }

    [Fact]
    public async Task LongDictate_ReachesTheRewriter_WithTheProfile_AndNoContext()
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        var fake = new FakeRewriter(_ => ["Done."]);
        _rig.Rewriter = fake;
        var result = await _rig.Build().RunAsync(Request(Mode.Dictate, Ctx("ignored"), Profile.Code), CancellationToken.None);

        Assert.Equal("Done.", result.Output);
        Assert.True(result.Rewritten);
        var sent = Assert.Single(fake.Requests);
        Assert.Equal(Profile.Code, sent.Profile);
        Assert.Null(sent.Context); // Dictate never sends context
    }

    [Theory]
    [InlineData(Mode.Dictate, DeliveryIntent.Paste, true)] // quiet too: the run says pasted or copied
    [InlineData(Mode.Prompt, DeliveryIntent.ClipboardOnly, true)]
    [InlineData(Mode.Reply, DeliveryIntent.ClipboardOnly, true)]
    public async Task Fallbacks_FollowTheSpec_AndClipboardCopiesStayQuiet(Mode mode, DeliveryIntent intent, bool quiet)
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        var result = await _rig.Build().RunAsync(Request(mode), CancellationToken.None);

        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
        Assert.Equal(RewriteFailure.Server, result.RewriteError);
        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(PipelineRig.LongTranscript, item.Text);
        Assert.Equal(intent, item.Intent);
        Assert.Equal(quiet, item.QuietCopy);
    }

    [Theory]
    [InlineData(Mode.Dictate, 5)]
    [InlineData(Mode.Prompt, 8)]
    [InlineData(Mode.Reply, 8)]
    public async Task NoFirstToken_TimesOut_PerMode_AndFallsBack(Mode mode, int seconds)
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        var stalling = new StallingRewriter();
        _rig.Rewriter = stalling;
        var run = _rig.Build().RunAsync(Request(mode), CancellationToken.None);
        await stalling.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _rig.Time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);
        Assert.False(run.IsCompleted);
        _rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
        Assert.Equal(RewriteFailure.Timeout, result.RewriteError);
    }

    [Fact]
    public async Task AStallBetweenDeltas_TimesOut_KeepingTheTranscript()
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        var stalling = new StallingRewriter("partial ");
        _rig.Rewriter = stalling;
        var run = _rig.Build().RunAsync(Request(Mode.Prompt), CancellationToken.None);
        await stalling.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _rig.Time.Advance(DictationPipeline.RewriteGap);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RewriteFailure.Timeout, result.RewriteError);
        Assert.Equal(PipelineRig.LongTranscript, result.Output); // the fallback is the transcript, not the partial
        Assert.NotEqual(RunOutcome.Interrupted, result.Outcome);
    }

    [Fact]
    public async Task ContextStillReading_NeverDelaysStt_AndIsAwaitedBeforeTheRewrite()
    {
        var pending = new TaskCompletionSource<CapturedContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeRewriter(_ => ["Sure."]);
        _rig.Rewriter = fake;
        var run = _rig.Build().RunAsync(Request(Mode.Reply, pending.Task), CancellationToken.None);

        await WaitUntil(() => _rig.Stt.Completions == 1);
        Assert.Empty(fake.Requests);
        pending.SetResult(new CapturedContext("Lunch Friday?", ContextSource.Selection, 0));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Lunch Friday?", Assert.Single(fake.Requests).Context!.Text);
        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal("Lunch Friday?", row.Context);
        Assert.Equal(ContextSource.Selection, row.ContextSource);
    }

    [Fact]
    public async Task AContextReadThatNeverFinishes_IsNoContext_AfterTheWait()
    {
        var never = new TaskCompletionSource<CapturedContext?>();
        var fake = new FakeRewriter(_ => ["Sure."]);
        _rig.Rewriter = fake;
        var run = _rig.Build().RunAsync(Request(Mode.Reply, never.Task), CancellationToken.None);
        await WaitUntil(() => _rig.Stt.Completions == 1);
        // Advance until the pipeline's context wait has been registered and fires; one jump could land before it.
        for (var i = 0; i < 50 && !run.IsCompleted; i++)
        {
            _rig.Time.Advance(DictationPipeline.ContextWait);
            await Task.Delay(20);
        }

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Null(Assert.Single(fake.Requests).Context);
    }

    [Fact]
    public async Task ASecretOnTheClipboard_IsSkipped_AndSaidSo()
    {
        var fake = new FakeRewriter(_ => ["Sure."]);
        _rig.Rewriter = fake;
        var result = await _rig.Build().RunAsync(Request(Mode.Reply, Ctx("key: ghp_" + new string('a', 36), ContextSource.Clipboard)), CancellationToken.None);

        Assert.Equal(ContextNote.Skipped, result.ContextNote);
        Assert.Null(Assert.Single(fake.Requests).Context);
        Assert.Null(_rig.History.Get(result.HistoryId!.Value)!.Context);
    }

    [Fact]
    public async Task ASecretInASelection_IsRedactedBeforeTheModelSeesIt()
    {
        var fake = new FakeRewriter(_ => ["Sure."]);
        _rig.Rewriter = fake;
        var result = await _rig.Build().RunAsync(Request(Mode.Reply, Ctx("my token ghp_" + new string('a', 36) + " ok")), CancellationToken.None);
        Assert.Equal("my token [redacted] ok", Assert.Single(fake.Requests).Context!.Text);
        Assert.Equal(ContextSource.Selection, result.ContextSource);
        Assert.Equal("my token [redacted] ok".Length, result.ContextChars);
    }

    [Fact]
    public async Task AReplyWithALink_IsPastedAsWritten()
    {
        // Bryan, 2026-10-28: no output guard; it's a tool for him alone.
        _rig.Rewriter = new FakeRewriter(_ => ["Sure: https://example.com/doc"]);
        await _rig.Build().RunAsync(Request(Mode.Reply, Ctx("Send me the link")), CancellationToken.None);
        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.Paste, item.Intent);
        Assert.False(item.QuietCopy);
    }

    [Fact]
    public async Task PromptAppendsTheSelection_ExactlyAsSelected()
    {
        // Prompt never sends the selection to the model, so nothing is redacted or normalised in what it attaches.
        var code = "const HASH = \"" + new string('e', 64) + "\"; // \uFB01 x\u00B2";
        _rig.Rewriter = new FakeRewriter(_ => ["Explain this."]);
        var result = await _rig.Build().RunAsync(Request(Mode.Prompt, Ctx(code)), CancellationToken.None);
        Assert.Contains(code, result.Output);
    }

    [Fact]
    public async Task PromptsAttachedSelection_LosesOnlyInvisibleCharacters()
    {
        // Tag characters and bidi controls could smuggle instructions to the AI the prompt is sent to.
        _rig.Rewriter = new FakeRewriter(_ => ["Explain this."]);
        var result = await _rig.Build().RunAsync(Request(Mode.Prompt, Ctx("x\u202E = \U000E0041\U000E0042\uFB01")), CancellationToken.None);
        Assert.Contains("x = \uFB01", result.Output);
        // Ordinal: culture comparison treats these characters as ignorable and "finds" them anywhere.
        Assert.DoesNotContain("\u202E", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\U000E0041", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptStillSkipsASecretOnTheClipboard()
    {
        _rig.Rewriter = new FakeRewriter(_ => ["Explain this."]);
        var result = await _rig.Build().RunAsync(Request(Mode.Prompt, Ctx("ghp_" + new string('a', 36), ContextSource.Clipboard)), CancellationToken.None);
        Assert.Equal(ContextNote.Skipped, result.ContextNote);
        Assert.DoesNotContain("ghp_", result.Output);
    }

    [Fact]
    public async Task APreviewMode_AsksForAPreview_WithoutShift()
    {
        _rig.Settings = _rig.Settings with { PreviewModes = ["Reply"] };
        _rig.Rewriter = new FakeRewriter(_ => ["Sure."]);
        await _rig.Build().RunAsync(Request(Mode.Reply), CancellationToken.None);
        Assert.True(Assert.Single(_rig.Sink.Delivered).PreviewRequested);
    }

    [Fact]
    public async Task ModelOutput_IsNormalised_BeforeDelivery()
    {
        _rig.Rewriter = new FakeRewriter(_ => ["Hi\u202E there\r\n", "\u200B"]);
        var result = await _rig.Build().RunAsync(Request(Mode.Reply), CancellationToken.None);
        Assert.Equal("Hi there", result.Output);
    }

    [Fact]
    public async Task WhitespaceOnlyOutput_IsAnEmptyOutputFallback()
    {
        _rig.Rewriter = new FakeRewriter(_ => ["  ", "\u200B\n"]);
        var result = await _rig.Build().RunAsync(Request(Mode.Reply), CancellationToken.None);
        Assert.Equal(RewriteFailure.EmptyOutput, result.RewriteError);
        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
