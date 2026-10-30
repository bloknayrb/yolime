using Yolime.Core.Audio;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Feedback;

public class NoticesTests
{
    private static RunResult Result(RunOutcome outcome, SttError? stt = null, CaptureError? capture = null, Mode mode = Mode.Dictate, DeliveryKind delivery = DeliveryKind.None) =>
        new() { RunId = Guid.NewGuid(), Outcome = outcome, Mode = mode, SttError = stt, CaptureError = capture, Delivery = delivery };

    [Fact]
    public void EveryCombination_MapsWithoutThrowing_AndHasCopy()
    {
        foreach (var outcome in Enum.GetValues<RunOutcome>())
        foreach (var stt in Enum.GetValues<SttError>().Cast<SttError?>().Append(null))
        foreach (var capture in Enum.GetValues<CaptureError>().Cast<CaptureError?>().Append(null))
        foreach (var mode in Enum.GetValues<Mode>())
        {
            if (Notices.For(Result(outcome, stt, capture, mode)) is { } n)
                Assert.False(string.IsNullOrWhiteSpace(Notices.Text(n)), $"{outcome}/{stt}/{capture} has no copy");
        }
        foreach (var kind in Enum.GetValues<NoticeKind>())
            Assert.False(string.IsNullOrWhiteSpace(Notices.Text(new Notice(kind, Mode.Dictate, 5))), $"{kind} has no copy");
    }

    [Theory]
    [InlineData(RunOutcome.Completed)]
    [InlineData(RunOutcome.Cancelled)]
    [InlineData(RunOutcome.Busy)]
    public void QuietOutcomes_HaveNoNotice(RunOutcome outcome) => Assert.Null(Notices.For(Result(outcome)));

    [Fact]
    public void Cancelled_WithAKeptRecording_SaysSo_ButADiscardIsSilent()
    {
        var kept = Result(RunOutcome.Cancelled) with { HistoryId = 7 };
        Assert.Equal(NoticeKind.Cancelled, Notices.For(kept)!.Kind);
        Assert.Null(Notices.For(Result(RunOutcome.Cancelled)));
    }

    [Fact]
    public void Fallback_IsQuiet_WhenPasted_ButCopiedResultsAreAnnounced()
    {
        Assert.Null(Notices.For(Result(RunOutcome.CompletedWithFallback, delivery: DeliveryKind.Pasted)));
        // Copied deliveries already showed the executor's notice; the run result adds nothing on top.
        Assert.Null(Notices.For(Result(RunOutcome.CompletedWithFallback, delivery: DeliveryKind.Copied)));
    }

    [Theory]
    [InlineData(SttError.MissingKey, NoticeKind.NoKey)]
    [InlineData(SttError.Auth, NoticeKind.KeyRejected)]
    [InlineData(SttError.QuotaExceeded, NoticeKind.OutOfCredit)]
    [InlineData(SttError.RateLimited, NoticeKind.RateLimited)]
    [InlineData(SttError.Network, NoticeKind.ServiceUnreachable)]
    [InlineData(SttError.Timeout, NoticeKind.ServiceUnreachable)]
    [InlineData(SttError.Server, NoticeKind.ServiceUnreachable)]
    [InlineData(SttError.InsecureEndpoint, NoticeKind.EndpointInvalid)]
    [InlineData(SttError.BadRequest, NoticeKind.TranscriptionFailed)]
    public void SttErrors_MapToSpecificNotices(SttError error, NoticeKind expected) =>
        Assert.Equal(expected, Notices.For(Result(RunOutcome.SttFailed, error))!.Kind);

    [Theory]
    [InlineData(CaptureError.AccessDenied, NoticeKind.MicDenied)]
    [InlineData(CaptureError.NoDevice, NoticeKind.NoMic)]
    [InlineData(CaptureError.DeviceLost, NoticeKind.MicLost)]
    public void CaptureErrors_MapToSpecificNotices(CaptureError error, NoticeKind expected) =>
        Assert.Equal(expected, Notices.For(Result(RunOutcome.CaptureFailed, capture: error))!.Kind);

    [Fact]
    public void NoSpeech_SaysSo_AndRetryableCopyMentionsTheTray()
    {
        var n = Notices.For(Result(RunOutcome.NoSpeech))!;
        Assert.Equal(NoticeKind.DidntHear, n.Kind);
        Assert.Contains("Retry", Notices.Text(Notices.For(Result(RunOutcome.SttFailed, SttError.Network))!));
    }

    /// <summary>
    /// Paused history keeps nothing (no history row, the recording deleted), so no failure offers Retry: the cards
    /// said "Your recording is kept: Retry" with no recording and no Retry button.
    /// </summary>
    [Theory]
    [InlineData(RunOutcome.SttFailed)]
    [InlineData(RunOutcome.NoSpeech)]
    [InlineData(RunOutcome.Interrupted)]
    [InlineData(RunOutcome.CaptureFailed)]
    public void NothingKept_OffersNoRetry(RunOutcome outcome)
    {
        var kept = Notices.For(Result(outcome, SttError.Network, CaptureError.DeviceLost) with { HistoryId = 3 })!;
        var lost = Notices.For(Result(outcome, SttError.Network, CaptureError.DeviceLost) with { HistoryPaused = true })!;
        // A row that failed to save (no id, not paused) keeps its recording for the next start's Housekeeping.
        Assert.Contains("Retry", Notices.Text(Notices.For(Result(outcome, SttError.Network, CaptureError.DeviceLost))!));
        Assert.Contains("Retry", Notices.Text(kept));
        Assert.DoesNotContain("Retry", Notices.Text(lost));
        Assert.DoesNotContain("Retry", Notices.Text(lost, onCard: true));
    }

    [Fact]
    public void RewriteFallback_OffersRetryLastFailed_OnlyWhenKept()
    {
        var kept = Result(RunOutcome.CompletedWithFallback, mode: Mode.Prompt, delivery: DeliveryKind.Copied) with { RewriteError = RewriteFailure.Timeout, HistoryId = 3 };
        Assert.Contains("Retry last failed in the tray", Notices.Text(Notices.For(kept)!));
        Assert.EndsWith("or Retry.", Notices.Text(Notices.For(kept)!, onCard: true));
        Assert.DoesNotContain("Retry", Notices.Text(Notices.For(kept with { HistoryId = null, HistoryPaused = true })!));
    }

    [Fact]
    public void BusyCopy_UsesModeNouns()
    {
        Assert.Contains("dictation", Notices.Text(new Notice(NoticeKind.Busy, Mode.Dictate)));
        Assert.Contains("prompt", Notices.Text(new Notice(NoticeKind.Busy, Mode.Prompt)));
    }

    [Theory]
    [InlineData(OutputNotice.None, null)]
    [InlineData(OutputNotice.Refused, NoticeKind.PasswordRefused)]
    [InlineData(OutputNotice.CopiedElevated, NoticeKind.CopiedAdminWindow)]
    [InlineData(OutputNotice.CopiedTargetChanged, NoticeKind.Copied)]
    [InlineData(OutputNotice.CopiedNotEditable, NoticeKind.Copied)]
    public void OutputNotices_Map(OutputNotice output, NoticeKind? expected) => Assert.Equal(expected, Notices.For(output)?.Kind);

    [Fact]
    public void RateLimiter_ShowsTheSameNoticeOncePer30s()
    {
        var limiter = new NoticeLimiter(TimeSpan.FromSeconds(30));
        var n = new Notice(NoticeKind.NoKey);
        Assert.True(limiter.ShouldShow(n, 0));
        Assert.False(limiter.ShouldShow(n, 10_000));
        Assert.True(limiter.ShouldShow(new Notice(NoticeKind.MicDenied), 10_000));
        Assert.True(limiter.ShouldShow(n, 30_000));
    }
}
