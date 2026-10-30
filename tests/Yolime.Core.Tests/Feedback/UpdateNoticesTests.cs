using Yolime.Core.Feedback;
using Yolime.Core.Overlay;
using Yolime.Core.Settings;
using Yolime.Core.Updates;

namespace Yolime.Core.Tests.Feedback;

public class UpdateNoticesTests
{
    private static readonly AppVersion V010 = new(0, 1, 0, false);
    private static readonly AppVersion V020 = new(0, 2, 0, false);

    [Fact]
    public void Texts_NameTheVersions()
    {
        Assert.Equal("yolime 0.2.0 is available. Click to install, or use the tray menu.",
            Notices.Text(new Notice(NoticeKind.UpdateAvailable, Version: V020)));
        Assert.Equal("yolime 0.2.0 is available, but this copy runs as administrator, so it can't install it. Exit and start yolime from the Start menu.",
            Notices.Text(new Notice(NoticeKind.UpdateAvailableElevated, Version: V020)));
        Assert.Equal("Updated to 0.2.0.", Notices.Text(new Notice(NoticeKind.Updated, Version: V020)));
        Assert.Equal("The update to 0.2.0 didn't install, so yolime is still on 0.1.0. Click to open the log.",
            Notices.Text(new Notice(NoticeKind.UpdateFailed, Version: V020, Current: V010, UpdateFailure: UpdateFailure.SwapFailed)));
    }

    [Fact]
    public void OnTheCard_TheFailure_DropsTheClickDirection()
    {
        Assert.Equal("The update to 0.2.0 didn't install; yolime is still on 0.1.0.",
            Notices.Text(new Notice(NoticeKind.UpdateFailed, Version: V020, Current: V010), onCard: true));
    }

    [Theory]
    [InlineData(NoticeKind.Started, true)]
    [InlineData(NoticeKind.UpdateAvailable, true)]
    [InlineData(NoticeKind.Updated, true)]
    [InlineData(NoticeKind.UpdateAvailableElevated, false)]
    [InlineData(NoticeKind.UpdateFailed, false)]
    [InlineData(NoticeKind.DidntHear, false)]
    public void BalloonIcons_SplitInfoFromWarnings(NoticeKind kind, bool info) => Assert.Equal(info, Notices.IsInfo(kind));

    [Fact]
    public void TheTooltip_SaysAnUpdateIsReady_BeforeAHotkeyProblem_WithinTheLimit()
    {
        Assert.Equal("yolime: ready", TrayState.Compose("yolime: ready", updateReady: false, hotkeyProblem: null));
        Assert.Equal("yolime: ready · update ready", TrayState.Compose("yolime: ready", true, null));
        Assert.Equal("yolime: ready · update ready · Win+Alt+V is taken by another app",
            TrayState.Compose("yolime: ready", true, "Win+Alt+V is taken by another app"));
        var longest = TrayState.Compose(new string('a', 120), true, "Win+Alt+V is taken by another app");
        Assert.Equal(127, longest.Length);
        Assert.Contains("update ready", longest, StringComparison.Ordinal);
    }
}

public class UpdatingOverlayTests
{
    private static readonly AppVersion V010 = new(0, 1, 0, false);
    private static readonly AppVersion V020 = new(0, 2, 0, false);
    private readonly OverlayModel _m = new(silenceRms: 0.01f);

    [Fact]
    public void Updating_IsItsOwnCard_NoButtons_PoliteNarration_NoAutoHide()
    {
        var v = _m.Apply(new UpdatingShown(V020, AtMs: 0));
        Assert.Equal(OverlayState.Updating, v.State);
        Assert.Equal("Updating to 0.2.0. yolime will close and reopen in a moment.", v.Title);
        Assert.Empty(v.Buttons);
        Assert.False(v.Clickable);
        Assert.Equal(v.Title, v.Narration);
        Assert.False(v.NarrateUrgently);
        Assert.Equal(OverlayState.Updating, _m.Apply(new Tick(3_600_000)).State);
    }

    [Fact]
    public void Updating_IsntReplacedByAnotherNotice_OrDismissed()
    {
        _m.Apply(new UpdatingShown(V020, 0));
        Assert.Equal(OverlayState.Updating, _m.Apply(new NoticeOnly(new Notice(NoticeKind.Busy), 10)).State);
        Assert.Equal(OverlayState.Updating, _m.Apply(new Dismissed()).State);
        var late = new Yolime.Core.Pipeline.RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.NoSpeech, Mode = Mode.Dictate };
        Assert.Equal(OverlayState.Updating, _m.Apply(new RunFinished(late, new Notice(NoticeKind.DidntHear), "x", 20)).State);
    }

    [Fact]
    public void AFailedInstall_BecomesAWarning_ThatOpensTheLog()
    {
        _m.Apply(new UpdatingShown(V020, 0));
        var v = _m.Apply(new UpdatingEnded(new Notice(NoticeKind.UpdateFailed, Version: V020, Current: V010), 100));
        Assert.Equal(OverlayState.Error, v.State);
        Assert.Equal("The update to 0.2.0 didn't install; yolime is still on 0.1.0.", v.Title);
        Assert.Equal([OverlayButton.OpenLog, OverlayButton.Dismiss], v.Buttons);
        Assert.True(v.Clickable);
    }

    [Fact]
    public void ASlowSetup_BecomesAWarning_ThatStaysUntilDismissed()
    {
        _m.Apply(new UpdatingShown(V020, 0));
        var v = _m.Apply(new UpdatingEnded(new Notice(NoticeKind.UpdateSlow, Version: V020), 300_000));
        Assert.Equal(OverlayState.Error, v.State);
        Assert.Contains(OverlayButton.Dismiss, v.Buttons);
        Assert.Equal(OverlayState.Error, _m.Apply(new Tick(300_000 + 3_600_000)).State);
        Assert.Equal(OverlayState.Hidden, _m.Apply(new Dismissed()).State);
    }

    [Fact]
    public void UpdatingEnded_WithNothingToSay_HidesTheCard()
    {
        _m.Apply(new UpdatingShown(V020, 0));
        Assert.Equal(OverlayState.Hidden, _m.Apply(new UpdatingEnded(null, 10)).State);
    }
}
