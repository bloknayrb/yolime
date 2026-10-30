using Yolime.Core.Overlay;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Overlay;

/// <summary>The Listening card while the mic hasn't delivered yet (the latency-leftovers plan, PR A).</summary>
public class MicStartingTests
{
    private const string Starting = "Mic starting…";
    private const string Muted = "No audio: is the mic muted?";
    private const string NotResponding = "Mic not responding";

    private readonly OverlayModel _m = new(silenceRms: 0.01f);
    private readonly Guid _run = Guid.NewGuid();

    private OverlayView Start(bool ready, Mode mode = Mode.Dictate, bool latched = false) =>
        _m.Apply(new RecordingStarted(mode, "Slack", latched, 0, _run, MicReady: ready));

    private OverlayView Detail(long at) => _m.Apply(new Tick(at));

    [Fact]
    public void ASlowMic_ShowsStarting_UntilItsAudioFlows()
    {
        Start(ready: false);
        Assert.Equal(Starting, Detail(400).Detail);
        Assert.Null(_m.Apply(new AudioFlowing(1_500, Delivered: true, _run)).Detail);
        Assert.Equal("Dictate · Slack", _m.Current.Title); // the title is the mode, which Shift changes: it stays put
    }

    [Fact]
    public void AMicThatIsReadyAtOnce_NeverShowsStarting()
    {
        Start(ready: true);
        Assert.Null(Detail(400).Detail);
    }

    [Fact]
    public void AMicAFewMillisecondsLate_DoesntFlashStarting()
    {
        // A cold USB mic often delivers within a few hundred ms: the line would flash for one frame.
        Start(ready: false);
        Assert.Null(Detail(200).Detail);
        _m.Apply(new AudioFlowing(250, Delivered: true, _run));
        Assert.Null(Detail(400).Detail);
    }

    [Fact]
    public void TheSilenceHint_CountsFromWhenTheMicWasReady()
    {
        // A headset ready at 1.9 s mustn't be called muted 100 ms later, before anyone could have spoken.
        Start(ready: false);
        _m.Apply(new AudioFlowing(1_900, Delivered: true, _run));
        Assert.NotEqual(Muted, Detail(2_100).Detail);
        Assert.NotEqual(Muted, Detail(3_899).Detail);
        Assert.Equal(Muted, Detail(3_900).Detail);
    }

    [Fact]
    public void WhileStarting_TheMutedHintIsHeldBack()
    {
        Start(ready: false);
        Assert.Equal(Starting, Detail(2_500).Detail);
    }

    [Fact]
    public void AMicThatDeliversJustAfterTheCap_ClearsNotResponding()
    {
        // A headset whose first buffer lands at 2.3 s: the start sound plays at the cap, he speaks, and the card
        // mustn't keep saying the mic isn't responding while it records fine.
        Start(ready: false);
        _m.Apply(new AudioFlowing(2_000, Delivered: false, _run));
        Assert.Null(_m.Apply(new Level(0.001f, 2_300)).Detail);
        Assert.Equal(Muted, Detail(4_300).Detail);
    }

    [Fact]
    public void AMicThatNeverDelivered_SaysSoAtOnce()
    {
        // The 2 s cap gave up waiting: no buffer at all is "not responding", not "muted" (a muted mic still delivers).
        Start(ready: false);
        Assert.Equal(NotResponding, _m.Apply(new AudioFlowing(2_000, Delivered: false, _run)).Detail);
        Assert.Equal(NotResponding, Detail(5_000).Detail);
    }

    [Fact]
    public void AnyLevel_MeansTheAudioIsFlowing()
    {
        // A backstop, should the flowing signal ever be missed.
        Start(ready: false);
        Assert.Null(_m.Apply(new Level(0.001f, 600)).Detail);
        Assert.Equal(Muted, Detail(2_600).Detail);
    }

    [Fact]
    public void TheCapCountdown_WinsOverStarting()
    {
        Start(ready: false);
        Detail(400);
        Assert.Equal("5 s left", _m.Apply(new CapCountdown(5, 400)).Detail);
    }

    [Fact]
    public void AModeChangeWhileStarting_KeepsIt()
    {
        Start(ready: false);
        Detail(400);
        var v = _m.Apply(new ModeChanged(Mode.Prompt));
        Assert.Equal("Prompt · Slack", v.Title);
        Assert.Equal(Starting, v.Detail);
    }

    [Fact]
    public void InReply_StartingCoversTheContextLine_UntilTheMicIsReady()
    {
        Start(ready: false, Mode.Reply);
        _m.Apply(new ContextShown("Are we on for Friday?", ContextSource.Selection, 0, false));
        Assert.Equal(Starting, Detail(400).Detail);
        Assert.StartsWith("Replying to:", _m.Apply(new AudioFlowing(900, Delivered: true, _run)).Detail);
    }

    [Fact]
    public void Latched_ShowsTapToStop_OnceReady()
    {
        Start(ready: false, latched: true);
        Assert.Equal(Starting, Detail(400).Detail);
        Assert.Equal("Tap to stop", _m.Apply(new AudioFlowing(800, Delivered: true, _run)).Detail);
    }

    [Fact]
    public void AnotherRunsFlowingSignal_IsIgnored()
    {
        Start(ready: false);
        _m.Apply(new AudioFlowing(500, Delivered: true, Guid.NewGuid()));
        Assert.Equal(Starting, Detail(600).Detail);
    }

    [Fact]
    public void AFlowingSignalAfterTheCardLeftListening_DoesNothing()
    {
        Start(ready: false);
        var working = _m.Apply(new RecordingEnded(700));
        Assert.Equal(working, _m.Apply(new AudioFlowing(800, Delivered: true, _run)));
    }
}
