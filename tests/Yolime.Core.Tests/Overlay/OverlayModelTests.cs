using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Overlay;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Overlay;

public class OverlayModelTests
{
    private readonly OverlayModel _m = new(silenceRms: 0.01f);

    private OverlayView Start(Mode mode = Mode.Dictate, string app = "Slack", bool latched = false, long at = 0) =>
        _m.Apply(new RecordingStarted(mode, app, latched, at));

    private static RunResult Result(RunOutcome outcome = RunOutcome.Completed, DeliveryKind delivery = DeliveryKind.Pasted, bool rewritten = true, long? historyId = 1) => new()
    {
        RunId = Guid.NewGuid(),
        Outcome = outcome,
        Mode = Mode.Dictate,
        Delivery = delivery,
        Rewritten = rewritten,
        HistoryId = historyId,
        Output = "one two three four",
        Timing = new Yolime.Core.Logging.RunTiming { RunId = Guid.NewGuid(), Mode = Mode.Dictate, StartedUtc = DateTimeOffset.UnixEpoch, Outcome = outcome, OutputWords = 4, OutputDoneMs = 1900 },
    };

    [Fact]
    public void Listening_ShowsModeAppAndElapsed_ClickThrough_NoNarration()
    {
        var v = Start(Mode.Reply, "Slack");
        Assert.Equal(OverlayState.Listening, v.State);
        Assert.Equal("Reply · Slack", v.Title);
        Assert.Equal("0:00", v.Elapsed);
        Assert.False(v.Clickable);
        Assert.Null(v.Narration); // anything spoken now would be recorded
        Assert.Equal("1:05", _m.Apply(new Tick(65_000)).Elapsed);
    }

    [Fact]
    public void TheAppName_CanArriveAfterTheNotchShows()
    {
        Assert.Equal("Dictate", Start(app: "").Title);
        Assert.Equal("Dictate · Word", _m.Apply(new AppIdentified("Word")).Title);
    }

    [Fact]
    public void ModeCycle_UpdatesTheTitle_Silently()
    {
        Start();
        var v = _m.Apply(new ModeChanged(Mode.Prompt));
        Assert.Equal("Prompt · Slack", v.Title);
        Assert.Null(v.Narration);
    }

    [Fact]
    public void NoVoiceFor2s_WarnsMicMuted_UntilVoiceArrives()
    {
        Start();
        _m.Apply(new Level(0.001f, 500));
        Assert.DoesNotContain("muted", _m.Apply(new Tick(1_999)).Detail ?? "");
        Assert.Contains("No audio: is the mic muted?", _m.Apply(new Tick(2_000)).Detail);
        _m.Apply(new Level(0.2f, 2_100));
        Assert.DoesNotContain("muted", _m.Apply(new Tick(2_200)).Detail ?? "");
    }

    [Fact]
    public void ContextLine_SaysWhatTheReplyIsAbout()
    {
        Start(Mode.Reply);
        var v = _m.Apply(new ContextShown("Are we still on for lunch Friday at the usual place downtown near the office?", ContextSource.Clipboard, 12_000, Skipped: false));
        Assert.StartsWith("Replying to: 'Are we still on for lunch", v.Detail);
        Assert.EndsWith("(copied 12 s ago)", v.Detail);
        Assert.True(v.Detail!.Length < 110);
        Assert.Equal("No context", _m.Apply(new ContextShown(null, null, 0, Skipped: false)).Detail);
        Assert.Contains("skipped", _m.Apply(new ContextShown(null, null, 0, Skipped: true)).Detail);
    }

    [Fact]
    public void Latched_SaysTapToStop_AndTheCapCountsDown()
    {
        Start(latched: true);
        Assert.Equal("Tap the hold key to stop", _m.Apply(new Tick(100)).Detail);
        Assert.Equal("10 s left", _m.Apply(new CapCountdown(10, 100)).Detail);
        Assert.Equal("4 s left", _m.Apply(new Tick(6_100)).Detail);
    }

    [Fact]
    public void Working_ShowsTranscribingThenWriting_WithTheStream()
    {
        Start();
        Assert.Equal("Transcribing…", _m.Apply(new RecordingEnded(3_000)).Title);
        _m.Apply(new TranscriptShown("hello there"));
        var v = _m.Apply(new StageBegan(Stage.Rewrite, 3_500));
        Assert.Equal("Writing for Slack…", v.Title);
        Assert.Equal("hello there", v.Detail);
        _m.Apply(new DeltaArrived("Hello", 3_600));
        _m.Apply(new DeltaArrived(" there.", 3_700));
        // The stream is throttled to 4 Hz: the view catches up on the next tick past 250 ms.
        Assert.Equal("Hello there.", _m.Apply(new Tick(3_900)).Detail);
        Assert.False(_m.Current.Clickable);
    }

    [Fact]
    public void Done_ShowsWhereWordsAndTime_ThenHides()
    {
        Start();
        var v = _m.Apply(new RunFinished(Result(rewritten: false), null, "Slack", 5_000));
        Assert.Equal(OverlayState.Done, v.State);
        Assert.Equal("→ Slack · 4 words · 1.9 s", v.Title);
        Assert.Equal("Pasted into Slack, 4 words", v.Narration);
        Assert.Equal(OverlayState.Done, _m.Apply(new Tick(6_199)).State);
        Assert.Equal(OverlayState.Hidden, _m.Apply(new Tick(6_200)).State);
    }

    [Fact]
    public void Done_WhenCopied_SaysSo_ForFourSeconds()
    {
        Start();
        var v = _m.Apply(new RunFinished(Result(delivery: DeliveryKind.Copied), null, "Slack", 5_000));
        Assert.Equal("Copied: press\u00a0Ctrl+V", v.Title);
        Assert.Equal(OverlayState.Done, _m.Apply(new Tick(8_999)).State);
        Assert.Equal(OverlayState.Hidden, _m.Apply(new Tick(9_000)).State);
    }

    [Fact]
    public void UseRaw_IsOffered5s_AfterARewrittenPaste_AndWithdrawnOnAnyKey()
    {
        Start();
        var v = _m.Apply(new RunFinished(Result(), null, "Slack", 5_000));
        Assert.True(v.OfferUseRaw);
        var pill = _m.Apply(new Tick(9_000)); // Done has gone after 1.2 s; the pill stays for 5 s
        Assert.Equal(OverlayState.UseRaw, pill.State);
        Assert.True(pill.OfferUseRaw);
        Assert.Equal(OverlayState.Hidden, _m.Apply(new Tick(10_000)).State);

        Start(at: 20_000);
        _m.Apply(new RunFinished(Result(), null, "Slack", 25_000));
        Assert.False(_m.Apply(new UserTyped()).OfferUseRaw);
    }

    [Fact]
    public void UseRaw_IsNotOffered_WithoutAHistoryRow()
    {
        // History paused: there is no row to take the raw transcript from.
        Start();
        Assert.False(_m.Apply(new RunFinished(Result(historyId: null), null, "Slack", 5_000)).OfferUseRaw);
    }

    [Theory]
    [InlineData(false, DeliveryKind.Pasted)]
    [InlineData(true, DeliveryKind.Copied)]
    public void UseRaw_IsNotOffered_ForUnrewrittenOrCopiedResults(bool rewritten, DeliveryKind delivery)
    {
        Start();
        Assert.False(_m.Apply(new RunFinished(Result(rewritten: rewritten, delivery: delivery), null, "Slack", 5_000)).OfferUseRaw);
    }

    [Fact]
    public void Error_IsClickable_WithRetryWhenRetryable_AndCardWording()
    {
        Start();
        var n = new Notice(NoticeKind.ServiceUnreachable);
        var v = _m.Apply(new RunFinished(Result(RunOutcome.SttFailed), n, "Slack", 5_000));
        Assert.Equal(OverlayState.Error, v.State);
        Assert.True(v.Clickable);
        Assert.Contains(OverlayButton.Retry, v.Buttons);
        Assert.Contains(OverlayButton.Dismiss, v.Buttons);
        Assert.Equal(1, v.HistoryId);
        Assert.DoesNotContain("from the tray", v.Narration); // the card has the button
        // The first sentence is the title and the rest the detail; Narrator hears all of it.
        Assert.Equal(v.Narration, v.Detail is null ? v.Title : v.Title + " " + v.Detail);
    }

    [Fact]
    public void Error_StaysLongerForLongerText_PausesOnHover_AndCaps()
    {
        Start();
        var v = _m.Apply(new RunFinished(Result(RunOutcome.SttFailed), new Notice(NoticeKind.ServiceUnreachable), "Slack", 0));
        var expected = Math.Min(15_000, 6_000 + 40 * v.Narration!.Length); // the whole text, not just the title
        _m.Apply(new PointerOver(true));
        Assert.Equal(OverlayState.Error, _m.Apply(new Tick(60_000)).State); // hovered: stays
        _m.Apply(new PointerOver(false));
        Assert.Equal(OverlayState.Error, _m.Apply(new Tick(60_000 + expected - 1)).State);
        Assert.Equal(OverlayState.Hidden, _m.Apply(new Tick(60_000 + expected)).State);
    }

    [Fact]
    public void MicDenied_OffersOpenSettings()
    {
        Start();
        var v = _m.Apply(new RunFinished(Result(RunOutcome.CaptureFailed, historyId: null), new Notice(NoticeKind.MicDenied), "Slack", 0));
        Assert.Contains(OverlayButton.OpenSettings, v.Buttons);
        Assert.DoesNotContain(OverlayButton.Retry, v.Buttons);
    }

    [Fact]
    public void ANewHold_ReplacesDoneAndError()
    {
        Start();
        _m.Apply(new RunFinished(Result(RunOutcome.SttFailed), new Notice(NoticeKind.ServiceUnreachable), "Slack", 0));
        Assert.Equal(OverlayState.Listening, Start(at: 1_000).State);
    }

    [Fact]
    public void Preview_NamesItsMode()
    {
        // A preview in the wrong mode was the bug that made the gesture misleading: say which one this is.
        Start(Mode.Prompt);
        var v = _m.Apply(new PreviewShown("Draft."));
        Assert.Equal("Preview \u00b7 Prompt", v.Title);
        Assert.StartsWith("Preview, Prompt. Enter to insert, C to copy, Escape to discard. ", v.Narration);
    }

    [Fact]
    public void Preview_IsClickable_Narrated_AndBlocksANewHold()
    {
        Start();
        var v = _m.Apply(new PreviewShown("The whole long output."));
        Assert.Equal(OverlayState.Preview, v.State);
        Assert.True(v.Clickable);
        Assert.Equal([OverlayButton.Insert, OverlayButton.Copy, OverlayButton.Discard], v.Buttons);
        Assert.Equal("The whole long output.", v.Preview);
        Assert.Contains("The whole long output.", v.Narration);
        Assert.Equal(OverlayState.Preview, Start(at: 2_000).State); // a hold during preview is Busy, not a new card
        Assert.Equal(OverlayState.Hidden, _m.Apply(new PreviewEnded()).State);
    }

    [Fact]
    public void PendingPaste_SaysClickWhereItShouldGo_WithACountdown()
    {
        Start();
        var v = _m.Apply(new PendingPasteStarted(10, 4_000));
        Assert.Equal(OverlayState.PendingPaste, v.State);
        Assert.Equal("Click where the reply should go", v.Title);
        Assert.Equal("10 s left, then it's copied", v.Detail);
        Assert.Equal("7 s left, then it's copied", _m.Apply(new Tick(7_000)).Detail);
        Assert.False(v.Clickable);
    }

    [Fact]
    public void AQuietRunEnd_DoesNotHideTheDeliverysOwnCard()
    {
        Start();
        _m.Apply(new NoticeOnly(new Notice(NoticeKind.PreviewKept), 1_000));
        var v = _m.Apply(new RunFinished(Result(RunOutcome.Discarded), null, "Slack", 1_100));
        Assert.Equal(OverlayState.Error, v.State);
        Assert.Contains("kept in history", v.Narration);
    }

    [Fact]
    public void StandaloneNotices_ShowBriefly_WithoutButtons()
    {
        var v = _m.Apply(new NoticeOnly(new Notice(NoticeKind.Busy, Mode.Prompt), 0));
        Assert.Equal(OverlayState.Error, v.State);
        Assert.Equal([OverlayButton.Dismiss], v.Buttons);
        Assert.Contains("prompt", v.Title);
    }

    /// <summary>The design pass's text fixes: titles stay one line, snippets end at a word, the clock has one format.</summary>
    [Fact]
    public void LongAppNames_AreShortened_SnippetsEndAtAWord_TheClockKeepsItsFormat()
    {
        const string app = "Contoso Enterprise Resource Planning Client (Finance Workspace)";
        var v = _m.Apply(new RecordingStarted(Mode.Reply, app, false, 0));
        Assert.Equal("Reply · Contoso Enterprise Resource Planning Cl…", v.Title);
        v = _m.Apply(new ContextShown("Can you send me the final Q3 numbers before Thursday's budget meeting?", ContextSource.Selection, 0, false));
        Assert.Equal("Replying to: 'Can you send me the final Q3 numbers before Thursday's…' (selected text)", v.Detail);
        _m.Apply(new Level(0.02f, 100));
        v = _m.Apply(new RecordingEnded(5_000));
        Assert.Equal("0:05", v.Elapsed);
        Assert.Equal("Transcribed", _m.Apply(new TranscriptShown("hello there")).Title);
    }

    /// <summary>A confirmation isn't a warning: Copied gets the info glyph and polite narration, Busy still warns.</summary>
    [Fact]
    public void ConfirmationCards_AreInformational()
    {
        var copied = _m.Apply(new NoticeOnly(new Notice(NoticeKind.Copied), 0));
        Assert.True(copied.Informational);
        Assert.False(copied.NarrateUrgently);
        var busy = _m.Apply(new NoticeOnly(new Notice(NoticeKind.Busy, Mode.Prompt), 0));
        Assert.False(busy.Informational);
        Assert.True(busy.NarrateUrgently);
    }

    [Fact]
    public void TheViewsTextNeverReachesToString()
    {
        Start();
        _m.Apply(new RecordingEnded(1));
        var v = _m.Apply(new TranscriptShown("SENTINEL_TRANSCRIPT"));
        var shown = _m.Apply(new PreviewShown("SENTINEL_PREVIEW")) with { Detail = "SENTINEL_DETAIL" };
        Assert.DoesNotContain("SENTINEL", v.ToString());
        Assert.DoesNotContain("SENTINEL", shown.ToString());
        Assert.Contains("Preview", shown.ToString()); // the state is still there for diagnostics
    }
}
