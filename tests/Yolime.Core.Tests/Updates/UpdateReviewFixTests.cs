using Yolime.Core.Feedback;
using Yolime.Core.Overlay;
using Yolime.Core.Settings;
using Yolime.Core.Updates;

namespace Yolime.Core.Tests.Updates;

/// <summary>The 2c UI/UX review's findings: what the user is told when the window is closed, or a dictation is on screen.</summary>
public class UpdateReviewFixTests
{
    private static readonly AppVersion V010 = new(0, 1, 0, false);
    private static readonly AppVersion V020 = new(0, 2, 0, false);
    private static readonly ReleaseInfo Release = new(V020, 101, 1000, 102);
    private static readonly UpdateState Offered = UpdateState.Initial(false).CheckStarted().CheckFinished(new CheckResult(CheckOutcome.Available, Release, "ABC"));
    private readonly OverlayModel _m = new(silenceRms: 0.01f);

    [Fact]
    public void WhileItInstalls_NotNow_SaysHide_BecauseThatsWhatItDoes()
    {
        Assert.Equal("Not now", UpdateTexts.DismissButton(Offered));
        Assert.Equal("Hide", UpdateTexts.DismissButton(Offered.InstallRequested(true)));
        Assert.Equal("Hide", UpdateTexts.DismissButton(Offered.InstallRequested(false)));
        Assert.Equal("Not now", UpdateTexts.DismissButton(Offered.InstallRequested(false).DownloadFinished(DownloadOutcome.Failed)));
    }

    [Fact]
    public void TheWindow_SaysWhyInstallIsOff_DuringACheck()
    {
        Assert.Equal("Checking for a newer version…", UpdateTexts.WindowStatus(Offered.CheckStarted()));
    }

    [Theory]
    [InlineData(DownloadOutcome.Failed, "Couldn't download yolime 0.2.0. Nothing changed. Click to try again.")]
    [InlineData(DownloadOutcome.ChecksumMismatch, "The yolime 0.2.0 download didn't match its checksum, so it wasn't installed. Click to try again.")]
    public void AFailureWithTheWindowClosed_GetsABalloon(DownloadOutcome outcome, string text)
    {
        var downloading = Offered.InstallRequested(false);
        var failed = downloading.DownloadFinished(outcome);
        var n = UpdateGate.UnseenFailure(downloading, failed);
        Assert.NotNull(n);
        Assert.Equal(NoticeKind.UpdateAttemptFailed, n.Kind);
        Assert.Equal(text, Notices.Text(n));
        Assert.False(Notices.IsInfo(n.Kind));
    }

    [Fact]
    public void ASetupThatWouldntStart_WithTheWindowClosed_GetsABalloon()
    {
        var downloading = Offered.InstallRequested(false);
        var n = UpdateGate.UnseenFailure(downloading, downloading.LaunchFailed());
        Assert.Equal("Couldn't start the yolime 0.2.0 setup. Nothing changed. Click to try again.", Notices.Text(n!));
    }

    [Fact]
    public void OnlyTheStepIntoAWindowFailure_GetsABalloon()
    {
        var downloading = Offered.InstallRequested(false);
        var failed = downloading.DownloadFinished(DownloadOutcome.Failed);
        Assert.Null(UpdateGate.UnseenFailure(Offered, downloading));
        Assert.Null(UpdateGate.UnseenFailure(failed, failed));
        // The card says these ones.
        var updating = downloading.DownloadFinished(DownloadOutcome.Ready).SetupLaunched(0);
        Assert.Null(UpdateGate.UnseenFailure(updating, updating.SetupExited()));
        Assert.Null(UpdateGate.UnseenFailure(updating, updating.Tick(10 * 60_000)));
    }

    [Fact]
    public void TheAdminNotice_DoesntUseUpTheOffer_ForTheSameVersion()
    {
        var s = new YolimeSettings();
        var (admin, afterAdmin) = UpdateGate.Announcement(V020, elevated: true, s);
        Assert.Equal(NoticeKind.UpdateAvailableElevated, admin?.Kind);
        Assert.Null(UpdateGate.Announcement(V020, elevated: true, afterAdmin).Notice);

        // Started again from the Start menu, as the admin notice said: the offer still shows, once.
        var (offer, afterOffer) = UpdateGate.Announcement(V020, elevated: false, afterAdmin);
        Assert.Equal(NoticeKind.UpdateAvailable, offer?.Kind);
        Assert.Equal(V020, offer?.Version);
        Assert.Null(UpdateGate.Announcement(V020, elevated: false, afterOffer).Notice);
    }

    [Fact]
    public void ALateSetupFailure_DoesntReplaceADictation()
    {
        var failed = new Notice(NoticeKind.UpdateFailed, Version: V020, Current: V010);
        _m.Apply(new RecordingStarted(Mode.Dictate, "Word", false, 0));
        Assert.Equal(OverlayState.Listening, _m.Apply(new UpdatingEnded(failed, 100)).State);
        _m.Apply(new RecordingEnded(200));
        Assert.Equal(OverlayState.Working, _m.Apply(new UpdatingEnded(failed, 300)).State);
    }

    [Fact]
    public void ALateSetupFailure_DoesntReplaceAPreview_OrAPendingPaste()
    {
        var failed = new Notice(NoticeKind.UpdateFailed, Version: V020, Current: V010);
        _m.Apply(new PreviewShown("Draft."));
        Assert.Equal(OverlayState.Preview, _m.Apply(new UpdatingEnded(failed, 100)).State);
        _m.Apply(new PreviewEnded());
        _m.Apply(new PendingPasteStarted(10, 200));
        Assert.Equal(OverlayState.PendingPaste, _m.Apply(new UpdatingEnded(failed, 300)).State);
    }

    [Fact]
    public void ARefusedHold_DoesntReplaceAPendingPaste_OrTheRunItWaitsFor()
    {
        var refused = new NoticeOnly(new Notice(NoticeKind.UpdateHoldRefused), 100);
        _m.Apply(new PendingPasteStarted(10, 0));
        Assert.Equal(OverlayState.PendingPaste, _m.Apply(refused).State);
        _m.Apply(new PendingPasteEnded());
        _m.Apply(new RecordingStarted(Mode.Dictate, "Word", false, 1_000));
        Assert.Equal(OverlayState.Listening, _m.Apply(refused with { AtMs = 1_100 }).State);
        _m.Apply(new RecordingEnded(2_000));
        Assert.Equal(OverlayState.Working, _m.Apply(refused with { AtMs = 2_100 }).State);
        // With nothing on screen, it says why the hold did nothing.
        var m2 = new OverlayModel(silenceRms: 0.01f);
        Assert.Equal(OverlayState.Error, m2.Apply(refused).State);
    }

    [Fact]
    public void ALateSetupFailure_WithdrawsTheSlowCard_ButNothingElse()
    {
        _m.Apply(new UpdatingShown(V020, 0));
        _m.Apply(new UpdatingEnded(new Notice(NoticeKind.UpdateSlow, Version: V020), 300_000));
        // The balloon says the failure; the card saying "taking longer" would now be wrong.
        Assert.Equal(OverlayState.Hidden, _m.Apply(new UpdateSlowWithdrawn()).State);

        var other = new OverlayModel(silenceRms: 0.01f);
        other.Apply(new NoticeOnly(new Notice(NoticeKind.DidntHear), 0));
        Assert.Equal(OverlayState.Error, other.Apply(new UpdateSlowWithdrawn()).State);
        other.Apply(new Dismissed());
        other.Apply(new RecordingStarted(Mode.Dictate, "Word", false, 1_000));
        Assert.Equal(OverlayState.Listening, other.Apply(new UpdateSlowWithdrawn()).State);
    }

    [Fact]
    public void AfterTheAdminNotice_TheStartMenuCopy_OwesTheOffer_AndChecksSoon()
    {
        var s = UpdateGate.Announcement(V020, elevated: true, new YolimeSettings()).Next;
        Assert.True(UpdateGate.OwedOffer(s, V010, elevated: false));
        Assert.False(UpdateGate.OwedOffer(s, V010, elevated: true));
        // Already on it, or already offered.
        Assert.False(UpdateGate.OwedOffer(s, V020, elevated: false));
        Assert.False(UpdateGate.OwedOffer(UpdateGate.Announcement(V020, false, s).Next, V010, elevated: false));
        Assert.False(UpdateGate.OwedOffer(new YolimeSettings(), V010, elevated: false));
    }
}

/// <summary>The 2c correctness review: a check refused mid-install must not end the schedule.</summary>
public class UpdateScheduleResumeTests
{
    private static readonly ReleaseInfo Release = new(new AppVersion(0, 2, 0, false), 101, 1000, 102);
    private static readonly UpdateState Offered = UpdateState.Initial(false).CheckStarted().CheckFinished(new CheckResult(CheckOutcome.Available, Release, "ABC"));

    [Fact]
    public void ChecksWait_ExactlyWhileACheckWouldBeRefused_ByAnInstall()
    {
        var downloading = Offered.InstallRequested(false);
        var updating = downloading.DownloadFinished(DownloadOutcome.Ready).SetupLaunched(0);
        var timedOut = updating.Tick(10 * 60_000);
        foreach (var s in new[] { Offered.InstallRequested(true), downloading, updating, timedOut })
        {
            Assert.True(s.InstallBusy);
            Assert.Same(s, s.CheckStarted());
        }
        // Leaving them is when the schedule is armed again.
        foreach (var s in new[] { Offered, downloading.DownloadFinished(DownloadOutcome.Failed), downloading.LaunchFailed(), timedOut.SetupExited() })
        {
            Assert.False(s.InstallBusy);
            Assert.NotSame(s, s.CheckStarted());
        }
    }
}
