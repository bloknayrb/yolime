using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Feedback;

public class RewriteNoticesTests
{
    private static RunResult Fallback(RewriteFailure f, Mode mode) => new()
    {
        RunId = Guid.NewGuid(),
        Outcome = RunOutcome.CompletedWithFallback,
        Mode = mode,
        RewriteError = f,
        Delivery = mode == Mode.Dictate ? DeliveryKind.Pasted : DeliveryKind.Copied,
        HistoryId = 1,
    };

    [Fact]
    public void EveryFailure_InEveryRewriteMode_HasANotice_SayingWhereTheTextWent()
    {
        foreach (var f in Enum.GetValues<RewriteFailure>())
        foreach (var mode in new[] { Mode.Dictate, Mode.Prompt, Mode.Reply })
        {
            var n = Notices.For(Fallback(f, mode));
            Assert.NotNull(n);
            var text = Notices.Text(n);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{f}/{mode}");
            if (mode == Mode.Dictate) Assert.Contains("pasted", text);
            else Assert.Contains("Ctrl+V", text);
        }
    }

    [Theory]
    [InlineData(RewriteFailure.MissingKey, NoticeKind.RewriteNoKey)]
    [InlineData(RewriteFailure.Auth, NoticeKind.RewriteKeyRejected)]
    [InlineData(RewriteFailure.Billing, NoticeKind.RewriteOutOfCredit)]
    [InlineData(RewriteFailure.Refusal, NoticeKind.RewriteRefused)]
    [InlineData(RewriteFailure.MaxTokens, NoticeKind.RewriteTooLong)]
    [InlineData(RewriteFailure.Timeout, NoticeKind.RewriteTimedOut)]
    [InlineData(RewriteFailure.Config, NoticeKind.RewriteConfig)]
    [InlineData(RewriteFailure.EmptyOutput, NoticeKind.RewriteEmpty)]
    [InlineData(RewriteFailure.Network, NoticeKind.RewriteUnavailable)]
    [InlineData(RewriteFailure.Server, NoticeKind.RewriteUnavailable)]
    [InlineData(RewriteFailure.RateLimited, NoticeKind.RewriteUnavailable)]
    [InlineData(RewriteFailure.Unavailable, NoticeKind.RewriteUnavailable)]
    public void Failures_MapToSpecificKinds(RewriteFailure f, NoticeKind expected) =>
        Assert.Equal(expected, Notices.For(Fallback(f, Mode.Prompt))!.Kind);

    [Fact]
    public void ADictateFallbackThatWasCopied_SaysCopied_NotPasted()
    {
        var n = Notices.For(Fallback(RewriteFailure.Server, Mode.Dictate) with { Delivery = DeliveryKind.Copied })!;
        Assert.Contains("Ctrl+V", Notices.Text(n));
        Assert.DoesNotContain("pasted", Notices.Text(n));
    }

    [Fact]
    public void NoKey_PointsToSettings() =>
        // Keys are added in Settings now, with no restart (PR5); the environment variable still works but isn't the way in.
        Assert.Contains("Settings, under Keys", Notices.Text(new Notice(NoticeKind.RewriteNoKey, Mode.Reply)));

    [Fact]
    public void PromptAndReplyFallbacks_OfferRetry() =>
        Assert.Contains("Retry last", Notices.Text(Notices.For(Fallback(RewriteFailure.Server, Mode.Reply))!));

    [Fact]
    public void ARetryThatCouldNotStart_SaysNothingChanged()
    {
        var r = new RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.RewriteFailed, Mode = Mode.Prompt, RewriteError = RewriteFailure.MissingKey };
        var n = Notices.For(r)!;
        Assert.Equal(NoticeKind.RewriteNoKey, n.Kind);
        Assert.Null(n.Mode);
        Assert.DoesNotContain("Ctrl+V", Notices.Text(n));
    }

    [Fact]
    public void ASkippedContext_IsSaid_WhenNothingElseIs()
    {
        var ok = new RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.Completed, Mode = Mode.Reply, ContextNote = ContextNote.Skipped };
        Assert.Equal(NoticeKind.ContextSkipped, Notices.For(ok)!.Kind);
        Assert.Null(Notices.For(ok with { ContextNote = ContextNote.None }));
        // A failure notice is the more important one.
        Assert.Equal(NoticeKind.RewriteTimedOut, Notices.For(Fallback(RewriteFailure.Timeout, Mode.Reply) with { ContextNote = ContextNote.Skipped })!.Kind);
    }

    [Fact]
    public void AFallbackWithoutAnError_StaysQuiet() =>
        Assert.Null(Notices.For(new RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.CompletedWithFallback, Mode = Mode.Prompt, Delivery = DeliveryKind.Copied }));
}
