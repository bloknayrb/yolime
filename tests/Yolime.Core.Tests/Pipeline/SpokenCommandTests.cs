using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Pipeline;

/// <summary>Every hold is Dictate; a spoken command at the start of the transcript picks Prompt, Reply or Raw.</summary>
public sealed class SpokenCommandTests : IDisposable
{
    private readonly PipelineRig _rig = new();
    private readonly FakeRewriter _rewriter = new(r => [r.Mode == Mode.Reply ? "Sounds good!" : "Rewritten."]);

    public SpokenCommandTests() => _rig.Rewriter = _rewriter;

    public void Dispose() => _rig.Dispose();

    /// <summary>A hold as the coordinator makes it: Dictate, with detection on.</summary>
    private static RunRequest Hold(string? context = null, IProgress<PipelineEvent>? progress = null, bool shiftHeld = false) =>
        new(PipelineRig.Speech(), Mode.Dictate, "slack", Task.FromResult(new RunIntent(Mode.Dictate, shiftHeld)), progress,
            Context: Task.FromResult<CapturedContext?>(context is null ? null : new CapturedContext(context, ContextSource.Selection, 0)),
            DetectCommand: true);

    private Task<RunResult> Run(string transcript, string? context = null, IProgress<PipelineEvent>? progress = null, bool shiftHeld = false)
    {
        _rig.Stt.Text = transcript;
        return _rig.Build().RunAsync(Hold(context, progress, shiftHeld), CancellationToken.None);
    }

    [Fact]
    public async Task WriteAReply_RunsReply_OnTheWordsAfterIt_AndHistoryKeepsEveryWord()
    {
        var progress = new ListProgress();
        var result = await Run("Write a reply. Sounds good, see you then.", "Lunch Friday?", progress);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(Mode.Reply, result.Mode);
        var sent = Assert.Single(_rewriter.Requests);
        Assert.Equal(Mode.Reply, sent.Mode);
        Assert.Equal("Sounds good, see you then.", sent.Transcript);
        Assert.Equal("Lunch Friday?", sent.Context!.Text);
        Assert.Equal("Sounds good, see you then.", result.RawText); // Copy raw: the words the reply was written from

        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal(Mode.Reply, row.Mode);
        Assert.Equal("Write a reply. Sounds good, see you then.", row.RawText);
        Assert.Equal("Lunch Friday?", row.Context);

        Assert.Contains(new ModeResolved(Mode.Reply, Spoken: true), progress.Events);
        Assert.Contains(new TranscriptReady("Sounds good, see you then."), progress.Events);
    }

    [Fact]
    public async Task NoCommand_IsDictate_AndKeepsNoContext()
    {
        var result = await Run("hello world", "Write a reply. This is context, not speech.");
        Assert.Equal(Mode.Dictate, result.Mode); // context can never pick a mode
        Assert.Empty(_rewriter.Requests); // short dictation: local cleanup
        Assert.Equal("Hello world.", Assert.Single(_rig.Sink.Delivered).Text);
        Assert.Null(_rig.History.Get(result.HistoryId!.Value)!.Context);
    }

    [Fact]
    public async Task AShortCommandedRun_IsNeverSkippedAsShortDictation()
    {
        var result = await Run("Reply. Thanks!", "Lunch Friday?");
        Assert.Equal(Mode.Reply, result.Mode);
        Assert.Equal("Thanks!", Assert.Single(_rewriter.Requests).Transcript);
    }

    [Fact]
    public async Task Raw_PastesTheRestAsHeard()
    {
        var result = await Run("Raw. hello there");
        Assert.Equal(Mode.Raw, result.Mode);
        Assert.Empty(_rewriter.Requests);
        Assert.Equal("hello there", Assert.Single(_rig.Sink.Delivered).Text);
    }

    [Fact]
    public async Task WriteAPrompt_SendsNoContextToTheModel_AndAttachesTheSelection()
    {
        var result = await Run("Write a prompt: " + PipelineRig.LongTranscript, "var x = 1;");
        Assert.Equal(Mode.Prompt, result.Mode);
        Assert.StartsWith("So I wanted", Assert.Single(_rewriter.Requests).Transcript);
        Assert.Contains("var x = 1;", Assert.Single(_rig.Sink.Delivered).Text);
    }

    [Theory]
    [InlineData("Write a prompt.")]
    [InlineData("Raw.")]
    [InlineData("Make this a prompt")]
    public async Task ACommandAlone_IsCommandOnly_NothingSent_NotRetryable(string transcript)
    {
        var result = await Run(transcript, "Lunch Friday?");

        Assert.Equal(RunOutcome.CommandOnly, result.Outcome);
        Assert.Empty(_rewriter.Requests);
        Assert.Empty(_rig.Sink.Delivered);
        Assert.Empty(_rig.Pending.List()); // a retry would hear the same command
        var row = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal(RunOutcome.CommandOnly, row.Outcome);
        Assert.Equal(transcript, row.RawText);
        Assert.Null(_rig.History.LatestRetryable());
        Assert.Equal(NoticeKind.CommandOnly, Notices.For(result)!.Kind);
    }

    [Fact]
    public async Task WriteAReplyAlone_DraftsFromTheMessage()
    {
        var result = await Run("Write a reply.", "Lunch Friday?");

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        var sent = Assert.Single(_rewriter.Requests);
        Assert.Equal("", sent.Transcript);
        Assert.Equal("Lunch Friday?", sent.Context!.Text);
        Assert.Equal("Sounds good!", Assert.Single(_rig.Sink.Delivered).Text);
        // The system prompt says what to do with no instructions (the prompt file returns nothing for an empty transcript).
        var built = new PromptBuilder(new Yolime.Core.Prompts.PromptLibrary(_rig.Root.Paths)).Build(sent);
        Assert.Contains("said only \"write a reply\"", built.System);
    }

    [Fact]
    public async Task WriteAReplyAlone_WithNothingToReplyTo_SaysSo()
    {
        var result = await Run("Write a reply");

        Assert.Equal(RunOutcome.CommandOnly, result.Outcome);
        Assert.Equal(Mode.Reply, result.Mode);
        Assert.Empty(_rewriter.Requests);
        Assert.Empty(_rig.Sink.Delivered);
        Assert.Equal(NoticeKind.NothingToReply, Notices.For(result)!.Kind);
    }

    [Fact]
    public async Task WriteAReplyAlone_WhoseRewriteFails_DeliversNothing_AndCanBeRewrittenAgain()
    {
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        var result = await Run("Write a reply.", "Lunch Friday?");

        Assert.Equal(RunOutcome.RewriteFailed, result.Outcome);
        Assert.Empty(_rig.Sink.Delivered); // no empty transcript pasted as a "fallback"
        Assert.NotNull(Notices.For(result));
        // The card offers Retry, and the tray's Retry last failed finds it.
        Assert.True(Yolime.Core.Overlay.RunRetry.IsRetryable(result));
        Assert.Equal(result.HistoryId, _rig.History.LatestRetryable()?.Id);

        _rig.Rewriter = _rewriter;
        var again = await _rig.Build().RunFromTranscriptAsync(result.HistoryId!.Value, null, CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, again.Outcome);
        Assert.Equal("", Assert.Single(_rewriter.Requests).Transcript);
    }

    [Fact]
    public async Task AFailedReplyRewrite_FallsBackToTheWordsAfterTheCommand()
    {
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        var result = await Run("Write a reply. Sounds good.", "Lunch Friday?");
        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
        Assert.Equal("Sounds good.", Assert.Single(_rig.Sink.Delivered).Text);

        _rig.Rewriter = _rewriter;
        var again = await _rig.Build().RunFromTranscriptAsync(result.HistoryId!.Value, null, CancellationToken.None);
        Assert.Equal("Sounds good.", Assert.Single(_rewriter.Requests).Transcript);
        Assert.Equal(Mode.Reply, again.Mode);
    }

    [Fact]
    public async Task AReRunAsDictate_KeepsTheCommandWords_AsReplyDropsThem()
    {
        var first = await Run("Write a reply to John by Monday about the budget numbers for this quarter.", "Lunch Friday?");
        Assert.Equal(Mode.Reply, first.Mode); // a misheard note: it became a Reply

        var asDictate = await _rig.Build().RerunAsync(first.HistoryId!.Value, Mode.Dictate, null, CancellationToken.None);
        Assert.Equal("Write a reply to John by Monday about the budget numbers for this quarter.", _rewriter.Requests[^1].Transcript);
        Assert.Equal(Mode.Dictate, asDictate.Mode);

        await _rig.Build().RerunAsync(first.HistoryId!.Value, Mode.Reply, null, CancellationToken.None);
        Assert.Equal("To John by Monday about the budget numbers for this quarter.", _rewriter.Requests[^1].Transcript);
    }

    [Fact]
    public async Task Retry_WithNoMode_HearsTheCommandAgain_WithAModeKeepsIt()
    {
        _rig.Stt.Throw = new SttException(SttError.Network);
        var failed = await Run("Write a prompt. " + PipelineRig.LongTranscript);
        Assert.Equal(RunOutcome.SttFailed, failed.Outcome);
        Assert.Equal(Mode.Dictate, failed.Mode);
        _rig.Stt.Throw = null;

        var retried = await _rig.Build().RetryAsync(failed.HistoryId!.Value, null, null, CancellationToken.None);
        Assert.Equal(Mode.Prompt, retried.Mode);
        Assert.StartsWith("So I wanted", _rewriter.Requests[^1].Transcript);
    }

    [Fact]
    public async Task Retry_AsTheCommandsOwnMode_StripsIt_AsAnotherModeKeepsTheWords()
    {
        _rig.Stt.Throw = new SttException(SttError.Network);
        var failed = await Run("x");
        _rig.Stt.Throw = null;
        _rig.Stt.Text = "Write a prompt. " + PipelineRig.LongTranscript;

        var asPrompt = await _rig.Build().RetryAsync(failed.HistoryId!.Value, Mode.Prompt, null, CancellationToken.None);
        Assert.Equal(Mode.Prompt, asPrompt.Mode);
        Assert.StartsWith("So I wanted", _rewriter.Requests[^1].Transcript);
    }

    [Fact]
    public async Task AlwaysPreview_AppliesToAModePickedByVoice()
    {
        _rig.Settings = _rig.Settings with { PreviewModes = ["Reply"] };
        await Run("Write a reply. Sounds good.", "Lunch Friday?");
        var delivered = Assert.Single(_rig.Sink.Delivered);
        Assert.True(delivered.PreviewRequested);
        Assert.Equal(Mode.Reply, delivered.Mode);
    }

    [Fact]
    public async Task ShiftHeldAtRelease_PreviewsTheSpokenMode()
    {
        await Run("Write a reply. Sounds good.", "Lunch Friday?", shiftHeld: true);
        var delivered = Assert.Single(_rig.Sink.Delivered);
        Assert.True(delivered.PreviewRequested);
        Assert.Equal(Mode.Reply, delivered.Mode);
    }

    [Fact]
    public async Task WithDetectionOff_ACommandIsJustWords()
    {
        _rig.Stt.Text = "Write a reply. Sounds good.";
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        Assert.Equal(Mode.Raw, result.Mode);
        Assert.Equal("Write a reply. Sounds good.", Assert.Single(_rig.Sink.Delivered).Text);
    }

    [Fact]
    public async Task Retry_InTheCommandsOwnMode_WithNothingAfterIt_IsCommandOnly()
    {
        _rig.Stt.Throw = new SttException(SttError.Network);
        var failed = await Run("x");
        _rig.Stt.Throw = null;
        _rig.Stt.Text = "Raw.";

        var retried = await _rig.Build().RetryAsync(failed.HistoryId!.Value, Mode.Raw, null, CancellationToken.None);
        Assert.Equal(RunOutcome.CommandOnly, retried.Outcome);
        Assert.Empty(_rig.Sink.Delivered); // never an empty paste
    }

    [Fact]
    public async Task RewritingFromTheTranscript_SaysWhichMode()
    {
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        var result = await Run("Write a reply. Sounds good.", "Lunch Friday?");
        _rig.Rewriter = _rewriter;
        var progress = new ListProgress();
        await _rig.Build().RunFromTranscriptAsync(result.HistoryId!.Value, progress, CancellationToken.None);
        Assert.Contains(new ModeResolved(Mode.Reply, Spoken: false), progress.Events);
    }
}
