using Yolime.Core.Updates;

namespace Yolime.Core.Tests.Updates;

public class UpdateFlowTests
{
    private static readonly AppVersion V020 = new(0, 2, 0, false);
    private static readonly ReleaseInfo Release = new(V020, 101, 1000, 102);
    private static readonly CheckResult Found = new(CheckOutcome.Available, Release, "ABC");

    private static UpdateState Offered(bool elevated = false) => UpdateState.Initial(elevated).CheckStarted().CheckFinished(Found);

    [Fact]
    public void StartsIdle_WithNothingToInstall_AndHoldsAllowed()
    {
        var s = UpdateState.Initial(elevated: false);
        Assert.Equal(UpdatePhase.Idle, s.Phase);
        Assert.False(s.CanInstall);
        Assert.False(s.RefuseHolds);
        Assert.Null(s.Offered);
    }

    [Fact]
    public void ACheckThatFindsANewerRelease_OffersIt()
    {
        var checking = UpdateState.Initial(false).CheckStarted();
        Assert.Equal(UpdatePhase.Checking, checking.Phase);
        var s = checking.CheckFinished(Found);
        Assert.Equal(UpdatePhase.Available, s.Phase);
        Assert.True(s.CanInstall);
        Assert.Equal(V020, s.Offered);
        Assert.Equal("ABC", s.SetupHash);
    }

    [Fact]
    public void UpToDate_ClearsAnEarlierOffer()
    {
        var s = Offered().CheckStarted().CheckFinished(new CheckResult(CheckOutcome.UpToDate));
        Assert.Equal(UpdatePhase.Idle, s.Phase);
        Assert.Null(s.Release);
        Assert.False(s.CanInstall);
    }

    [Fact]
    public void TryLater_KeepsWhatWasKnown()
    {
        Assert.Equal(UpdatePhase.Idle, UpdateState.Initial(false).CheckStarted().CheckFinished(new CheckResult(CheckOutcome.TryLater)).Phase);
        var s = Offered().CheckStarted().CheckFinished(new CheckResult(CheckOutcome.TryLater));
        Assert.Equal(UpdatePhase.Available, s.Phase);
        Assert.Equal(V020, s.Offered);
    }

    [Fact]
    public void AnElevatedCopy_KnowsTheRelease_ButCanNeverInstallIt()
    {
        var s = Offered(elevated: true);
        Assert.Equal(V020, s.Release!.Version);
        Assert.False(s.CanInstall);
        Assert.Null(s.Offered);
        Assert.Same(s, s.InstallRequested(dictationInProgress: false));
    }

    [Fact]
    public void Install_Downloads_RefusingHolds_AndASecondClickDoesNothing()
    {
        var s = Offered().InstallRequested(dictationInProgress: false);
        Assert.Equal(UpdatePhase.Downloading, s.Phase);
        Assert.True(s.RefuseHolds);
        Assert.False(s.CanInstall);
        Assert.Same(s, s.InstallRequested(dictationInProgress: false));
    }

    [Fact]
    public void Install_DuringADictation_WaitsForItFirst()
    {
        var s = Offered().InstallRequested(dictationInProgress: true);
        Assert.Equal(UpdatePhase.Finishing, s.Phase);
        Assert.True(s.RefuseHolds);
        Assert.Equal(UpdatePhase.Downloading, s.DictationFinished().Phase);
    }

    [Fact]
    public void NoCheckStarts_InTheMiddleOfAnInstall()
    {
        var downloading = Offered().InstallRequested(false);
        Assert.Same(downloading, downloading.CheckStarted());
        var updating = downloading.SetupLaunched(nowMs: 0);
        Assert.Same(updating, updating.CheckStarted());
    }

    [Fact]
    public void Progress_IsKept_AndClamped()
    {
        var s = Offered().InstallRequested(false).DownloadProgress(0.4);
        Assert.Equal(0.4, s.Progress);
        Assert.Equal(1, s.DownloadProgress(7).Progress);
        Assert.Equal(0.4, s.DownloadProgress(0.2).Progress);
    }

    [Theory]
    [InlineData(DownloadOutcome.Failed, UpdateProblem.DownloadFailed)]
    [InlineData(DownloadOutcome.TooLarge, UpdateProblem.DownloadFailed)]
    [InlineData(DownloadOutcome.ChecksumMismatch, UpdateProblem.ChecksumMismatch)]
    [InlineData(DownloadOutcome.WrongVersion, UpdateProblem.ChecksumMismatch)]
    public void AFailedDownload_IsShown_HoldsComeBack_AndItCanBeTriedAgain(DownloadOutcome outcome, UpdateProblem problem)
    {
        var s = Offered().InstallRequested(false).DownloadFinished(outcome);
        Assert.Equal(UpdatePhase.Failed, s.Phase);
        Assert.Equal(problem, s.Problem);
        Assert.False(s.RefuseHolds);
        Assert.True(s.CanInstall);
        Assert.Equal(V020, s.Offered);
        Assert.Equal(UpdatePhase.Downloading, s.InstallRequested(false).Phase);
        Assert.Null(s.InstallRequested(false).Problem);
    }

    [Fact]
    public void AReadyDownload_WaitsForTheLaunch()
    {
        var s = Offered().InstallRequested(false).DownloadFinished(DownloadOutcome.Ready);
        Assert.Equal(UpdatePhase.Downloading, s.Phase);
        Assert.Equal(1, s.Progress);
        var launched = s.SetupLaunched(nowMs: 5_000);
        Assert.Equal(UpdatePhase.Updating, launched.Phase);
        Assert.True(launched.RefuseHolds);
        Assert.False(launched.CanInstall);
    }

    [Fact]
    public void ASetupThatWouldntStart_IsAFailure_ThatCanBeRetried()
    {
        var s = Offered().InstallRequested(false).DownloadFinished(DownloadOutcome.Ready).LaunchFailed();
        Assert.Equal(UpdatePhase.Failed, s.Phase);
        Assert.Equal(UpdateProblem.LaunchFailed, s.Problem);
        Assert.True(s.CanInstall);
    }

    [Fact]
    public void SetupExitingWhileYolimeRuns_IsAFailedInstall_HoldsComeBack()
    {
        var s = Offered().InstallRequested(false).SetupLaunched(0).SetupExited();
        Assert.Equal(UpdatePhase.Failed, s.Phase);
        Assert.Equal(UpdateProblem.InstallFailed, s.Problem);
        Assert.False(s.RefuseHolds);
        Assert.True(s.CanInstall);
    }

    [Fact]
    public void FiveMinutesWithoutAnExit_GiveTheHoldsBack_ButDontOfferASecondSetup()
    {
        var updating = Offered().InstallRequested(false).SetupLaunched(nowMs: 1_000);
        Assert.Same(updating, updating.Tick(1_000 + 299_999));
        var late = updating.Tick(1_000 + 300_000);
        Assert.Equal(UpdatePhase.Failed, late.Phase);
        Assert.Equal(UpdateProblem.TimedOut, late.Problem);
        Assert.False(late.RefuseHolds);
        // Setup may still be running (it isn't killed), and its mutex would refuse a second one.
        Assert.False(late.CanInstall);
        // If it then exits without having asked yolime to go, that's a failed install like any other.
        Assert.Equal(UpdateProblem.InstallFailed, late.SetupExited().Problem);
    }

    [Fact]
    public void OutOfOrderEvents_ChangeNothing()
    {
        var idle = UpdateState.Initial(false);
        Assert.Same(idle, idle.DownloadFinished(DownloadOutcome.Ready));
        Assert.Same(idle, idle.SetupLaunched(0));
        Assert.Same(idle, idle.SetupExited());
        Assert.Same(idle, idle.DictationFinished());
        Assert.Same(idle, idle.CheckFinished(Found));
        var offered = Offered();
        Assert.Same(offered, offered.Tick(10_000_000));
    }
}

public class UpdateTextsTests
{
    private static readonly AppVersion V020 = new(0, 2, 0, false);
    private static readonly ReleaseInfo Release = new(V020, 101, 1000, 102);
    private static readonly UpdateState Offered = UpdateState.Initial(false).CheckStarted().CheckFinished(new CheckResult(CheckOutcome.Available, Release, "ABC"));

    [Fact]
    public void TheWindow_AsksAndExplains()
    {
        Assert.Equal("Install yolime 0.2.0?", UpdateTexts.WindowTitle(V020));
        Assert.Equal("Installing closes yolime briefly and reopens it.", UpdateTexts.WindowStatus(Offered));
        Assert.Equal("Install now", UpdateTexts.InstallButton(Offered));
    }

    [Fact]
    public void TheWindow_FollowsTheInstall()
    {
        Assert.Equal("Finishing your dictation first…", UpdateTexts.WindowStatus(Offered.InstallRequested(true)));
        Assert.Equal("Downloading 0.2.0… 40%", UpdateTexts.WindowStatus(Offered.InstallRequested(false).DownloadProgress(0.4)));
        Assert.Equal("Checking the download…", UpdateTexts.WindowStatus(Offered.InstallRequested(false).DownloadFinished(DownloadOutcome.Ready)));
    }

    [Fact]
    public void TheWindow_SaysWhyItFailed_AndOffersToTryAgain()
    {
        var failed = Offered.InstallRequested(false).DownloadFinished(DownloadOutcome.Failed);
        Assert.Equal("Couldn't download 0.2.0. Nothing changed.", UpdateTexts.WindowStatus(failed));
        Assert.Equal("Try again", UpdateTexts.InstallButton(failed));
        Assert.Equal("The 0.2.0 download didn't match its checksum, so it wasn't installed.",
            UpdateTexts.WindowStatus(Offered.InstallRequested(false).DownloadFinished(DownloadOutcome.ChecksumMismatch)));
        Assert.Equal("Couldn't start the 0.2.0 setup. Nothing changed.", UpdateTexts.WindowStatus(Offered.InstallRequested(false).LaunchFailed()));
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Settings_NotInstalled_SaysUpdatesAreOff()
    {
        Assert.Equal("Updates are off for this copy (not installed)", UpdateTexts.SettingsLine(installed: false, Offered, Now, Now, null));
    }

    [Fact]
    public void Settings_ShowsTheLastResult()
    {
        var idle = UpdateState.Initial(false);
        Assert.Equal("Up to date · checked 2 h ago", UpdateTexts.SettingsLine(true, idle, Now, Now.AddHours(-2), null));
        Assert.Equal("Up to date · checked just now", UpdateTexts.SettingsLine(true, idle, Now, Now.AddSeconds(-20), null));
        Assert.Equal("Up to date · checked 5 min ago", UpdateTexts.SettingsLine(true, idle, Now, Now.AddMinutes(-5), null));
        Assert.Equal("Up to date · checked 3 days ago", UpdateTexts.SettingsLine(true, idle, Now, Now.AddDays(-3), null));
        Assert.Equal("Not checked yet", UpdateTexts.SettingsLine(true, idle, Now, null, null));
        Assert.Equal("Couldn't reach GitHub", UpdateTexts.SettingsLine(true, idle, Now, Now.AddHours(-3), Now.AddMinutes(-1)));
        Assert.Equal("Checking…", UpdateTexts.SettingsLine(true, idle.CheckStarted(), Now, null, null));
        Assert.Equal("0.2.0 is available", UpdateTexts.SettingsLine(true, Offered, Now, Now, null));
    }

    [Fact]
    public void Settings_AnElevatedCopy_SaysWhyItWontInstall()
    {
        var elevated = UpdateState.Initial(true).CheckStarted().CheckFinished(new CheckResult(CheckOutcome.Available, Release, "ABC"));
        Assert.Equal("0.2.0 is available, but this copy runs as administrator, so it can't install it", UpdateTexts.SettingsLine(true, elevated, Now, Now, null));
    }

    [Fact]
    public void TheReleasePage_IsBuiltFromTheVersion()
    {
        Assert.Equal("https://github.com/bloknayrb/yolime/releases/tag/v0.2.0", UpdateTexts.ReleasePage("bloknayrb", "yolime", V020));
    }
}
