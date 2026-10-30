using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Overlay;
using Yolime.Core.Pipeline;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Overlay;

/// <summary>Bryan's design-pass decisions (docs/superpowers/plans/2026-10-30-design-decisions.md), as rules.</summary>
public class DesignDecisionsTests
{
    private readonly OverlayModel _m = new(silenceRms: 0.01f);

    private static RunResult Run(RunOutcome outcome, Mode mode = Mode.Dictate, long? historyId = 1, SttError? stt = null, RewriteFailure? rewrite = null,
        DeliveryKind delivery = DeliveryKind.Copied) => new()
    {
        RunId = Guid.NewGuid(), Outcome = outcome, Mode = mode, HistoryId = historyId, SttError = stt, RewriteError = rewrite, Delivery = delivery,
    };

    // ---- cards-9: key and settings problems open Settings, and say where to retry when the run was kept ----

    public static TheoryData<RunResult> SettingsRuns() =>
    [
        Run(RunOutcome.SttFailed, stt: SttError.MissingKey),
        Run(RunOutcome.SttFailed, stt: SttError.Auth),
        Run(RunOutcome.SttFailed, stt: SttError.InvalidEndpoint),
        Run(RunOutcome.CompletedWithFallback, Mode.Prompt, rewrite: RewriteFailure.MissingKey),
        Run(RunOutcome.CompletedWithFallback, Mode.Reply, rewrite: RewriteFailure.Auth),
        Run(RunOutcome.CompletedWithFallback, Mode.Prompt, rewrite: RewriteFailure.Config),
        Run(RunOutcome.CompletedWithFallback, Mode.Dictate, rewrite: RewriteFailure.Auth, delivery: DeliveryKind.Pasted),
    ];

    [Theory]
    [MemberData(nameof(SettingsRuns))]
    public void SettingsKinds_OfferSettingsNotRetry_AndSayWhereToRetryWhenKept(RunResult run)
    {
        var notice = Notices.For(run)!;
        Assert.NotNull(Notices.SettingsPage(notice.Kind));
        var v = _m.Apply(new RunFinished(run, notice, "Slack", 0));
        Assert.Equal([OverlayButton.OpenYolimeSettings, OverlayButton.Dismiss], v.Buttons);
        Assert.Equal(Notices.SettingsPage(notice.Kind), v.Page);
        var card = Notices.Text(notice, onCard: true);
        Assert.DoesNotContain("or Retry", card);
        Assert.Equal(RunRetry.IsRetryable(run), card.Contains("choose Retry last failed in the tray menu", StringComparison.Ordinal));
        // Paused history keeps nothing: then no "kept" line either.
        var paused = Notices.Text(Notices.For(run with { HistoryPaused = true })!, onCard: true);
        Assert.DoesNotContain("is kept", paused);
    }

    [Fact]
    public void EveryKind_GetsOpenSettingsExactlyWhenItHasAPage()
    {
        foreach (var kind in Enum.GetValues<NoticeKind>())
        {
            var m = new OverlayModel(0.01f);
            var v = m.Apply(new NoticeOnly(new Notice(kind), 0));
            if (v.State != OverlayState.Error) continue;
            var expected = Notices.SettingsPage(kind) is not null && kind != NoticeKind.HoldKeyReset;
            Assert.True(expected == v.Buttons.Contains(OverlayButton.OpenYolimeSettings), kind.ToString());
            if (expected) Assert.DoesNotContain(OverlayButton.Retry, v.Buttons);
        }
    }

    [Fact]
    public void KeyErrors_NoLongerRetryable_OnTheCard_ButOtherFailuresStillAre()
    {
        var keyed = _m.Apply(new RunFinished(Run(RunOutcome.SttFailed, stt: SttError.Auth), Notices.For(Run(RunOutcome.SttFailed, stt: SttError.Auth)), "Slack", 0));
        Assert.DoesNotContain(OverlayButton.Retry, keyed.Buttons);
        var network = Run(RunOutcome.SttFailed, stt: SttError.Network);
        Assert.Contains(OverlayButton.Retry, _m.Apply(new RunFinished(network, Notices.For(network), "Slack", 1)).Buttons);
    }

    // ---- cards-11: title and detail ----

    [Fact]
    public void EveryCard_SplitsIntoTitleAndDetail_WithoutLosingText()
    {
        foreach (var kind in Enum.GetValues<NoticeKind>())
            foreach (Mode? mode in new Mode?[] { null, Mode.Dictate, Mode.Prompt, Mode.Reply })
                foreach (var copied in new[] { false, true })
                    foreach (var notKept in new[] { false, true })
                        foreach (var retryable in new[] { false, true })
                        {
                            var text = Notices.Text(new Notice(kind, mode, Copied: copied, NotKept: notKept, Retryable: retryable), onCard: true);
                            var (title, detail) = OverlayModel.SplitCard(text);
                            Assert.Equal(text, detail is null ? title : title + " " + detail);
                            Assert.False(string.IsNullOrWhiteSpace(title));
                        }
    }

    [Theory]
    [InlineData("Updated to 0.1.1.", "Updated to 0.1.1.", null)]
    [InlineData("Didn't hear anything. Is the mic muted?", "Didn't hear anything.", "Is the mic muted?")]
    [InlineData("Couldn't download yolime 0.1.1. Nothing changed.", "Couldn't download yolime 0.1.1.", "Nothing changed.")]
    [InlineData("Updating to 0.1.1. yolime will close.", "Updating to 0.1.1. yolime will close.", null)]
    public void SplitCard_AtASentenceBeforeACapital(string text, string title, string? detail) =>
        Assert.Equal((title, detail), OverlayModel.SplitCard(text));

    [Fact]
    public void UpdateSlow_StillWithdraws_AfterTheSplit_AndNothingElseDoes()
    {
        var v = new AppVersionTestHelper();
        _m.Apply(new UpdatingShown(v.Next, 0));
        var slow = _m.Apply(new UpdatingEnded(new Notice(NoticeKind.UpdateSlow, Version: v.Next), 1));
        Assert.Equal(OverlayState.Error, slow.State);
        Assert.Equal(OverlayState.Hidden, _m.Apply(new UpdateSlowWithdrawn()).State);
        // Another card that replaced it isn't withdrawn.
        _m.Apply(new UpdatingShown(v.Next, 2));
        _m.Apply(new UpdatingEnded(new Notice(NoticeKind.UpdateSlow, Version: v.Next), 3));
        _m.Apply(new NoticeOnly(new Notice(NoticeKind.Busy, Mode.Dictate), 4));
        Assert.Equal(OverlayState.Error, _m.Apply(new UpdateSlowWithdrawn()).State);
    }

    private sealed class AppVersionTestHelper
    {
        public Yolime.Core.Updates.AppVersion Next { get; } = new(0, 1, 1, false);
    }

    // ---- cards-10: one phrasing ----

    [Fact]
    public void Copied_IsSaidOneWay_AndCtrlVNeverSplitsFromPress()
    {
        foreach (var text in new[]
        {
            Notices.Text(new Notice(NoticeKind.Copied)),
            Notices.Text(new Notice(NoticeKind.CopiedAdminWindow)),
            Notices.Text(new Notice(NoticeKind.RawCopied)),
            Notices.Text(new Notice(NoticeKind.RewriteTimedOut, Mode.Dictate, Copied: true)),
            _m.Apply(new RunFinished(Run(RunOutcome.Completed, delivery: DeliveryKind.Copied), null, "Slack", 0)).Title,
        })
        {
            Assert.Contains("copied: press Ctrl+V", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("press Ctrl+V", text);
        }
        Assert.DoesNotContain("type into", Notices.Text(new Notice(NoticeKind.CopiedAdminWindow)));
    }

    // ---- cards-8: Esc dismisses a card ----

    [Fact]
    public void Esc_DismissesACard_AndNothingElse()
    {
        _m.Apply(new NoticeOnly(new Notice(NoticeKind.Copied), 0));
        Assert.Equal(OverlayState.Hidden, _m.Apply(new EscPressed()).State);
        _m.Apply(new RecordingStarted(Mode.Dictate, "Slack", false, 10));
        Assert.Equal(OverlayState.Listening, _m.Apply(new EscPressed()).State);
        _m.Apply(new PreviewShown("text"));
        Assert.Equal(OverlayState.Preview, _m.Apply(new EscPressed()).State);
    }

    // ---- overlay-14 and the tap ----

    [Fact]
    public void Tap_RetriesARetryableCard_CopiesRawFromThePill_ElseNothing()
    {
        var failed = Run(RunOutcome.SttFailed, stt: SttError.Network, historyId: 5);
        _m.Apply(new RunFinished(failed, Notices.For(failed), "Slack", 0));
        Assert.Equal(new TapAction(TapKind.Retry, 5), _m.Tap());

        var pasted = Run(RunOutcome.Completed, Mode.Prompt, historyId: 6, delivery: DeliveryKind.Pasted) with { Rewritten = true };
        _m.Apply(new RunFinished(pasted, null, "Slack", 100));
        Assert.Equal(new TapAction(TapKind.CopyRaw, 6), _m.Tap());

        _m.Apply(new Dismissed());
        Assert.Equal(TapAction.None, _m.Tap());
        var keyed = Run(RunOutcome.SttFailed, stt: SttError.Auth, historyId: 7);
        _m.Apply(new RunFinished(keyed, Notices.For(keyed), "Slack", 200));
        Assert.Equal(TapAction.None, _m.Tap()); // no Retry button there
    }

    [Fact]
    public void Labels_NameTheTapOnlyWhenItReachesTheCard()
    {
        Assert.Equal("Retry (tap Right Ctrl)", ButtonLabels.For(OverlayButton.Retry, "Right Ctrl", tapReaches: true));
        Assert.Equal("Retry", ButtonLabels.For(OverlayButton.Retry, "Right Ctrl", tapReaches: false));
        Assert.Equal("Copy raw (tap Pause)", ButtonLabels.Pill("Pause", tapReaches: true));
        Assert.Equal("Copy raw", ButtonLabels.Pill("Pause", tapReaches: false));
        Assert.Equal("Dismiss (Esc)", ButtonLabels.For(OverlayButton.Dismiss, "x", true));
        Assert.Equal("Open yolime Settings", ButtonLabels.For(OverlayButton.OpenYolimeSettings, "x", true));
    }

    [Fact]
    public void TapToStop_NamesTheHoldKey()
    {
        _m.HoldKeyName = "Right Shift";
        Assert.Equal("Tap Right Shift to stop", _m.Apply(new RecordingStarted(Mode.Dictate, "Slack", true, 0)).Detail);
    }

    // ---- overlay-5: the preview's clock ----

    [Fact]
    public void PreviewHint_ShowsSecondsOnlyInTheLastTen_AndPaused()
    {
        var v = _m.Apply(new PreviewShown("text", 20_000));
        Assert.Equal("Enter insert · C copy · Esc discard", v.Detail);
        Assert.Equal("8 s · Enter insert · C copy · Esc discard", _m.Apply(new PreviewClock(7_400, Paused: false)).Detail);
        Assert.Equal("Paused · Enter insert · C copy · Esc discard", _m.Apply(new PreviewClock(7_400, Paused: true)).Detail);
        Assert.Equal("Enter insert · C copy · Esc discard", _m.Apply(new PreviewClock(15_000, Paused: false)).Detail);
    }

    // ---- overlay-15: one width while listening ----

    [Fact]
    public void ListeningAndWorking_KeepOneWidth_OtherStatesSizeToContent()
    {
        Assert.True(_m.Apply(new RecordingStarted(Mode.Dictate, "Slack", false, 0)).FixedWidth);
        Assert.True(_m.Apply(new ContextShown("hello", ContextSource.Selection, 0, false)).FixedWidth);
        Assert.True(_m.Apply(new RecordingEnded(1_000)).FixedWidth);
        Assert.False(_m.Apply(new RunFinished(Run(RunOutcome.Completed, delivery: DeliveryKind.Pasted), null, "Slack", 2_000)).FixedWidth);
        Assert.False(_m.Apply(new NoticeOnly(new Notice(NoticeKind.Copied), 3_000)).FixedWidth);
    }
}
