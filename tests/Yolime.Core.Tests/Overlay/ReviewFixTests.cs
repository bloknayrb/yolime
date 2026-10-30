using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Overlay;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Overlay;

/// <summary>Regressions from the PR4 final review.</summary>
public class ReviewFixTests
{
    [Fact]
    public async Task OverlayProgress_PassesLevels_OnTheRealClock()
    {
        // Starting the throttle at long.MinValue overflowed "now - last" and dropped every level: a flat meter, and
        // "No audio: is the mic muted?" on every recording.
        var seen = new List<OverlayInput>();
        var progress = new OverlayProgress(seen.Add, TimeProvider.System);
        progress.Report(new CaptureLevel(0.2f, 0));
        await Task.Delay(60);
        progress.Report(new CaptureLevel(0.3f, 60));
        Assert.Equal(2, seen.OfType<Level>().Count());
    }

    [Fact]
    public void AnotherRunsResult_NeverReplacesTheCurrentRecordingOrWork()
    {
        // An old run unwinding late, or a retry finishing, must not land on the run the user is recording now.
        var m = new OverlayModel(0.01f);
        var current = Guid.NewGuid();
        m.Apply(new RecordingStarted(Mode.Dictate, "Slack", false, 0, current));
        var other = new RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.SttFailed, Mode = Mode.Dictate, HistoryId = 3 };
        Assert.Equal(OverlayState.Listening, m.Apply(new RunFinished(other, new Notice(NoticeKind.ServiceUnreachable), "Slack", 100)).State);
        m.Apply(new RecordingEnded(200));
        Assert.Equal(OverlayState.Working, m.Apply(new RunFinished(other with { Outcome = RunOutcome.Cancelled }, null, "Slack", 300)).State);
        // Its own result still lands, including an error while the key is still held (the mic was lost).
        var own = other with { RunId = current };
        Assert.Equal(OverlayState.Error, m.Apply(new RunFinished(own, new Notice(NoticeKind.MicLost), "Slack", 400)).State);
    }

    [Fact]
    public void AResultNeverHidesAnOpenPreview_TheHookStillHoldsItsKeys()
    {
        var m = new OverlayModel(0.01f);
        m.Apply(new RecordingStarted(Mode.Raw, "Slack", false, 0, Guid.NewGuid()));
        m.Apply(new PreviewShown("text"));
        var retry = new RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.Completed, Mode = Mode.Dictate, Delivery = DeliveryKind.Copied };
        Assert.Equal(OverlayState.Preview, m.Apply(new RunFinished(retry, null, "", 100)).State);
    }
}
