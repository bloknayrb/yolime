using System.Text;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Overlay;

/// <summary><see cref="UseRaw"/>: Done has gone, but the small "Use raw" pill is still on offer.</summary>
/// <see cref="Updating"/>: setup is running; the card stays until yolime exits or setup fails.
public enum OverlayState { Hidden, Listening, Working, Done, UseRaw, Error, Preview, PendingPaste, Updating }

public enum OverlayButton { Retry, Dismiss, OpenSettings, Insert, Copy, Discard, OpenLog, OpenYolimeSettings }

/// <summary>
/// What the notch shows. Holds content (transcripts, previews, context snippets), so its text never reaches
/// <see cref="object.ToString"/>, logs or E2E results: only the state and buttons do.
/// </summary>
/// <param name="Narration">What Narrator should announce for this view, or null. Never set while recording.</param>
/// <param name="OfferUseRaw">The "Use raw" pill: shown beside Done for 5 s after a rewritten paste.</param>
/// <param name="Informational">A card that confirms rather than warns: the info glyph instead of the warning one.</param>
/// <param name="Page">For a card with Open yolime Settings: the page it opens.</param>
/// <param name="FixedWidth">Listening and Working keep the card's full width, so a line arriving mid-recording doesn't resize it.</param>
public sealed record OverlayView(
    OverlayState State,
    Mode? Mode,
    string Title,
    string? Detail,
    string? Elapsed,
    float Level,
    string? Preview,
    IReadOnlyList<OverlayButton> Buttons,
    bool Clickable,
    string? Narration,
    bool OfferUseRaw,
    long? HistoryId,
    bool NarrateUrgently,
    bool Informational = false,
    SettingsPage? Page = null,
    bool FixedWidth = false)
{
    public static readonly OverlayView Hidden = new(OverlayState.Hidden, null, "", null, null, 0, null, [], false, null, false, null, false);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("State = ").Append(State).Append(", Mode = ").Append(Mode)
            .Append(", Buttons = [").Append(string.Join(", ", Buttons)).Append("], Clickable = ").Append(Clickable)
            .Append(", OfferUseRaw = ").Append(OfferUseRaw);
        return true;
    }
}

public abstract record OverlayInput;

/// <param name="RunId">The run this recording becomes: results from any other run (late, or a retry) don't land on it.</param>
/// <param name="MicReady">Whether the mic had delivered its first audio when the card showed.</param>
public sealed record RecordingStarted(Mode Mode, string AppName, bool Latched, long AtMs, Guid RunId = default, bool MicReady = true) : OverlayInput;

/// <summary>
/// The mic of a card that started not ready: <paramref name="Delivered"/> when its first audio arrived, false when the
/// 2 s wait gave up with nothing. A mic that fails or stops first also reports delivered (the devices end their first-
/// audio wait that way); that run then ends with its own MicLost card.
/// </summary>
public sealed record AudioFlowing(long AtMs, bool Delivered, Guid RunId) : OverlayInput;

public sealed record ModeChanged(Mode Mode) : OverlayInput;

/// <summary>The target app's name, once the target lookup (which runs beside the mic) has finished.</summary>
public sealed record AppIdentified(string AppName) : OverlayInput;

public sealed record Level(float Rms, long AtMs) : OverlayInput;

/// <summary>The context the run will use, already filtered. <paramref name="Skipped"/>: a clipboard held a secret.</summary>
public sealed record ContextShown(string? Snippet, ContextSource? Source, long AgeMs, bool Skipped) : OverlayInput;

/// <summary>The recording cap is <paramref name="SecondsLeft"/> away; the countdown then runs off ticks.</summary>
public sealed record CapCountdown(int SecondsLeft, long AtMs) : OverlayInput;

public sealed record RecordingEnded(long AtMs) : OverlayInput;

public sealed record StageBegan(Stage Stage, long AtMs) : OverlayInput;

public sealed record TranscriptShown(string Text) : OverlayInput;

public sealed record DeltaArrived(string Text, long AtMs) : OverlayInput;

public sealed record RunFinished(RunResult Result, Notice? Notice, string AppName, long AtMs) : OverlayInput;

/// <summary>A notice outside a run: Busy, Discarded, CapReached, PasswordRefused.</summary>
public sealed record NoticeOnly(Notice Notice, long AtMs) : OverlayInput;

/// <param name="RemainingMs">How long the hook will hold the preview's keys, from now (the hook's clock is the one that acts).</param>
public sealed record PreviewShown(string Text, long RemainingMs = Hotkeys.PreviewCapture.DeadlineMs) : OverlayInput;

/// <summary>The hook's preview deadline, as time left: pushed when the shown second or the hover pause changes.</summary>
public sealed record PreviewClock(long RemainingMs, bool Paused) : OverlayInput;

/// <summary>Esc, seen by the hook and passed on to the user's app: it dismisses a card that's showing.</summary>
public sealed record EscPressed : OverlayInput;

public sealed record PreviewEnded : OverlayInput;

public sealed record PendingPasteStarted(int Seconds, long AtMs) : OverlayInput;

public sealed record PendingPasteEnded : OverlayInput;

public sealed record PointerOver(bool Over) : OverlayInput;

/// <summary>The user pressed any key: a "Use raw" offer about the last paste no longer applies.</summary>
public sealed record UserTyped : OverlayInput;

public sealed record Dismissed : OverlayInput;

/// <summary>Setup has started: the Updating card replaces whatever is showing and nothing else replaces it.</summary>
public sealed record UpdatingShown(Updates.AppVersion Version, long AtMs) : OverlayInput;

/// <summary>Setup failed or ran too long while yolime is still up: the card becomes that notice (or goes, if none).</summary>
public sealed record UpdatingEnded(Notice? Notice, long AtMs) : OverlayInput;

/// <summary>Setup exited after the deadline: a "taking longer" card still up is now wrong (a balloon says why).</summary>
public sealed record UpdateSlowWithdrawn : OverlayInput;

public sealed record Tick(long NowMs) : OverlayInput;

/// <summary>
/// The notch's rules as a reducer: inputs in, the view out. No timers or threads; time arrives as <see cref="Tick"/>,
/// so every rule (silence after 2 s, Done for 1.2 s, the error card's length-scaled time) is testable.
/// </summary>
public sealed class OverlayModel(float silenceRms)
{
    private const long SilenceMs = 2_000;
    private const long DoneMs = 1_200;
    private const long CopiedMs = 4_000;
    private const long UseRawMs = 5_000;
    private const long StreamThrottleMs = 250;
    private const int SnippetChars = 60;

    private Mode _mode;
    private string _app = "";
    private bool _latched;
    private long _startMs;
    private long? _lastVoiceMs;
    // The mic: whether it has delivered, since when (the silence hint counts from there), and whether it gave up.
    private bool _micReady = true, _micDead;
    private long _readyAtMs;
    // A mic only a little late (a cold USB open) mustn't flash "Mic starting…" for one frame.
    private const long StartingGraceMs = 300;
    private string? _contextLine;
    private long? _capEndMs;
    private string? _transcript;
    private readonly StringBuilder _stream = new();
    private long _lastStreamShownMs;
    private long _hideAtMs;
    private bool _hovered;
    private long _hoverPausedMs;
    private long _useRawUntilMs;
    private long _pendingUntilMs;
    private long _nowMs;
    private Guid _runId;
    private string? _slowTitle;

    /// <summary>The hold key's name, for "Tap Right Ctrl to stop"; the controller keeps it current from settings.</summary>
    public string HoldKeyName { get; set; } = "the hold key";

    /// <summary>
    /// A result that mustn't replace what's on screen: an open preview or pending paste (the hook may hold keys for
    /// it), or a recording or its work that belongs to a different run than the one finishing.
    /// </summary>
    private bool BelongsElsewhere(RunResult r) =>
        Current.State is OverlayState.Preview or OverlayState.PendingPaste or OverlayState.Updating
        || (Current.State is OverlayState.Listening or OverlayState.Working && _runId != Guid.Empty && r.RunId != _runId);

    public OverlayView Current { get; private set; } = OverlayView.Hidden;

    public OverlayView Apply(OverlayInput input)
    {
        switch (input)
        {
            case RecordingStarted s:
                if (Current.State is OverlayState.Preview or OverlayState.Updating) break; // a hold during preview is Busy, not a new card
                (_mode, _app, _latched, _startMs, _nowMs, _runId) = (s.Mode, s.AppName, s.Latched, s.AtMs, s.AtMs, s.RunId);
                (_lastVoiceMs, _contextLine, _capEndMs, _transcript, _useRawUntilMs) = (null, null, null, null, 0);
                (_micReady, _micDead, _readyAtMs) = (s.MicReady, false, s.AtMs);
                _stream.Clear();
                Current = Listening();
                break;
            case AppIdentified a:
                _app = a.AppName;
                if (Current.State == OverlayState.Listening) Current = Listening() with { Level = Current.Level };
                break;
            case ModeChanged m when Current.State == OverlayState.Listening:
                _mode = m.Mode;
                Current = Listening();
                break;
            case AudioFlowing a when Current.State == OverlayState.Listening && (a.RunId == _runId || _runId == Guid.Empty):
                _nowMs = Math.Max(_nowMs, a.AtMs);
                if (a.Delivered) (_micReady, _readyAtMs) = (true, a.AtMs);
                else if (!_micReady) _micDead = true;
                Current = Listening() with { Level = Current.Level };
                break;
            case Level l when Current.State == OverlayState.Listening:
                // Any buffer means the mic is delivering: should the flowing signal be missed, or the mic start just
                // after the 2 s wait gave up ("Mic not responding" then clears).
                if (!_micReady) (_micReady, _micDead, _readyAtMs) = (true, false, l.AtMs);
                if (l.Rms >= silenceRms) _lastVoiceMs = l.AtMs;
                Current = Listening() with { Level = l.Rms };
                break;
            case ContextShown c when Current.State == OverlayState.Listening:
                // A selection is current, so only the clipboard's age is worth saying.
                _contextLine = c.Skipped ? "Copied text skipped (looks like a password or key)"
                    : c.Snippet is null ? "No context"
                    : $"Replying to: '{Snip(c.Snippet)}' ({(c.Source == ContextSource.Clipboard ? $"copied {c.AgeMs / 1000} s ago" : "selected text")})";
                Current = Listening();
                break;
            case CapCountdown cap when Current.State == OverlayState.Listening:
                _capEndMs = cap.AtMs + cap.SecondsLeft * 1000L;
                Current = Listening();
                break;
            case RecordingEnded e when Current.State == OverlayState.Listening:
                _nowMs = e.AtMs;
                Current = Working("Transcribing…", null);
                break;
            case TranscriptShown t when Current.State == OverlayState.Working:
                _transcript = LastLine(t.Text);
                // The text is here: it's no longer being transcribed.
                Current = Current with { Title = Current.Title == "Transcribing…" ? "Transcribed" : Current.Title, Detail = _transcript };
                break;
            case StageBegan { Stage: Stage.Rewrite } b when Current.State == OverlayState.Working:
                _nowMs = b.AtMs;
                Current = Working(_app.Length == 0 ? "Writing…" : $"Writing for {ShortApp(_app)}…", _transcript);
                break;
            case DeltaArrived d when Current.State == OverlayState.Working:
                _stream.Append(d.Text);
                if (d.AtMs - _lastStreamShownMs >= StreamThrottleMs && _lastStreamShownMs != 0) ShowStream(d.AtMs);
                else if (_lastStreamShownMs == 0) _lastStreamShownMs = d.AtMs;
                break;
            case RunFinished f when !BelongsElsewhere(f.Result):
                Current = Finished(f.Result, f.Notice, f.AppName, f.AtMs);
                break;
            // A refused hold mustn't cover the run the install is waiting for, or a pending paste's instruction.
            case NoticeOnly { Notice.Kind: NoticeKind.UpdateHoldRefused } when Current.State is OverlayState.Listening or OverlayState.Working or OverlayState.PendingPaste:
                break;
            case NoticeOnly n when Current.State is not (OverlayState.Preview or OverlayState.Updating):
                Current = Card(n.Notice, null, retryable: false, n.AtMs, minimum: 3_000);
                break;
            case PreviewShown p:
                // Named with its mode: a held Shift previews the mode the user is in, and the card should say so.
                Current = new OverlayView(OverlayState.Preview, _mode, "Preview \u00b7 " + _mode, PreviewHint(p.RemainingMs, paused: false), null, 0, p.Text,
                    [OverlayButton.Insert, OverlayButton.Copy, OverlayButton.Discard], true, $"Preview, {_mode}. Enter to insert, C to copy, Escape to discard. " + p.Text, false, null, false);
                break;
            case PreviewClock c when Current.State == OverlayState.Preview:
                Current = Current with { Detail = PreviewHint(c.RemainingMs, c.Paused) };
                break;
            case EscPressed when Current.State == OverlayState.Error:
                Current = OverlayView.Hidden;
                break;
            case PreviewEnded when Current.State == OverlayState.Preview:
            case PendingPasteEnded when Current.State == OverlayState.PendingPaste:
            case Dismissed when Current.State != OverlayState.Updating:
                Current = OverlayView.Hidden;
                break;
            case UpdatingShown u:
                _nowMs = u.AtMs;
                var updating = $"Updating to {u.Version}: yolime will close and reopen in a moment.";
                Current = new OverlayView(OverlayState.Updating, null, updating, null, null, 0, null, [], false, updating, false, null, false);
                break;
            // After the deadline holds are back, so setup's late exit can find a dictation on screen: it keeps its card
            // (the Updater says the failure in a balloon instead).
            case UpdatingEnded e when e.Notice is { } n
                && Current.State is not (OverlayState.Listening or OverlayState.Working or OverlayState.Preview or OverlayState.PendingPaste):
                Current = Card(n, null, retryable: false, e.AtMs, minimum: 6_000);
                break;
            case UpdatingEnded when Current.State == OverlayState.Updating:
            case UpdateSlowWithdrawn when Current.State == OverlayState.Error && _slowTitle is { } slow && Current.Title == slow:
                Current = OverlayView.Hidden;
                break;
            case PendingPasteStarted pp:
                _pendingUntilMs = pp.AtMs + pp.Seconds * 1000L;
                Current = new OverlayView(OverlayState.PendingPaste, _mode, "Click where the reply should go", PendingLeft(pp.Seconds), null, 0, null,
                    [], false, "Click where the reply should go", false, null, false);
                break;
            case PointerOver po when Current.State == OverlayState.Error:
                // Hovered time is added up tick by tick, so the card's clock stops while the mouse is on it.
                _hovered = po.Over;
                break;
            case UserTyped when Current.OfferUseRaw:
                Current = Current.State == OverlayState.UseRaw ? OverlayView.Hidden : Current with { OfferUseRaw = false };
                break;
            case Tick t:
                OnTick(t.NowMs);
                break;
        }
        return Current;
    }

    private void OnTick(long now)
    {
        if (_hovered) _hoverPausedMs += now - _nowMs;
        _nowMs = now;
        switch (Current.State)
        {
            case OverlayState.Listening:
                Current = Listening() with { Level = Current.Level };
                break;
            case OverlayState.Working:
                if (_stream.Length > 0 && now - _lastStreamShownMs >= StreamThrottleMs) ShowStream(now);
                Current = Current with { Elapsed = Minutes(now - _startMs) };
                break;
            case OverlayState.PendingPaste:
                Current = Current with { Detail = PendingLeft(Math.Max(0, (_pendingUntilMs - now + 999) / 1000)) };
                break;
            case OverlayState.Done or OverlayState.Error:
                if (Current.OfferUseRaw && now >= _useRawUntilMs) Current = Current with { OfferUseRaw = false };
                if (!_hovered && now >= _hideAtMs + _hoverPausedMs)
                    Current = Current.OfferUseRaw ? UseRawOnly() : OverlayView.Hidden;
                break;
            case OverlayState.UseRaw:
                if (now >= _useRawUntilMs) Current = OverlayView.Hidden;
                break;
        }
    }

    private OverlayView UseRawOnly() => OverlayView.Hidden with { State = OverlayState.UseRaw, OfferUseRaw = true, HistoryId = Current.HistoryId };

    private OverlayView Finished(RunResult r, Notice? notice, string app, long at)
    {
        _nowMs = at;
        if (notice is { } n) return Card(n, r.HistoryId, RunRetry.IsRetryable(r), at, minimum: 6_000);
        // A run that ended quietly (discarded, cancelled with nothing kept) never hides a card the delivery itself
        // just showed, such as "Preview closed: kept in history".
        if (r.Outcome is not (RunOutcome.Completed or RunOutcome.CompletedWithFallback))
            return Current.State is OverlayState.Error or OverlayState.Listening ? Current : OverlayView.Hidden;
        var copied = r.Delivery == DeliveryKind.Copied;
        var seconds = (r.Timing?.OutputDoneMs ?? 0) / 1000.0;
        var words = r.Timing?.OutputWords ?? 0;
        var title = copied ? "Copied: press\u00a0Ctrl+V"
            : $"→ {ShortApp(app)} · {words} words · {seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s";
        // Narrator reads words, not the arrow and dots.
        var narration = copied ? "Copied. Press Ctrl+V to paste." : app.Length == 0 ? $"Pasted, {words} words" : $"Pasted into {app}, {words} words";
        _hideAtMs = at + (copied ? CopiedMs : DoneMs);
        (_hovered, _hoverPausedMs) = (false, 0);
        var offer = r.Rewritten && r.Delivery == DeliveryKind.Pasted && r.HistoryId is not null;
        _useRawUntilMs = offer ? at + UseRawMs : 0;
        return new OverlayView(OverlayState.Done, r.Mode, title, null, null, 0, null, [], false, narration, offer, r.HistoryId, false);
    }

    private OverlayView Card(Notice n, long? historyId, bool retryable, long at, long minimum)
    {
        var text = Notices.Text(n, onCard: true);
        // A key or settings problem: retrying fails the same way until it's fixed, so the card opens Settings instead.
        var page = Notices.SettingsPage(n.Kind);
        var buttons = new List<OverlayButton>();
        if (retryable && page is null) buttons.Add(OverlayButton.Retry);
        if (n.Kind == NoticeKind.MicDenied) buttons.Add(OverlayButton.OpenSettings);
        if (page is not null && n.Kind != NoticeKind.HoldKeyReset) buttons.Add(OverlayButton.OpenYolimeSettings);
        if (n.Kind is NoticeKind.UpdateFailed or NoticeKind.UpdateSlow) buttons.Add(OverlayButton.OpenLog);
        buttons.Add(OverlayButton.Dismiss);
        // A setup still running after five minutes stays said until the user dismisses it.
        _hideAtMs = n.Kind == NoticeKind.UpdateSlow ? long.MaxValue / 4 : at + Math.Min(15_000, Math.Max(minimum, 6_000 + 40L * text.Length));
        // The first sentence is the title, the rest the smaller line below; narration keeps the whole text.
        var (title, detail) = SplitCard(text);
        if (n.Kind == NoticeKind.UpdateSlow) _slowTitle = title;
        (_hovered, _hoverPausedMs, _useRawUntilMs) = (false, 0, 0);
        // Good news and confirmations (Copied, Cancelled) get the info glyph and polite narration, as their balloons do.
        var info = Notices.IsInfo(n.Kind);
        return new OverlayView(OverlayState.Error, n.Mode ?? _mode, title, detail, null, 0, null, buttons, true, text, false, historyId, !info, info,
            page is not null && n.Kind != NoticeKind.HoldKeyReset ? page : null);
    }

    /// <summary>
    /// The first sentence and the rest, split at ". ", "? " or "! " before a capital, so "0.1.1. yolime" and "e.g. x"
    /// stay whole. One sentence: no detail.
    /// </summary>
    public static (string Title, string? Detail) SplitCard(string text)
    {
        for (var i = 0; i + 2 < text.Length; i++)
        {
            if (text[i] is '.' or '?' or '!' && text[i + 1] == ' ' && char.IsUpper(text[i + 2]))
                return (text[..(i + 1)], text[(i + 2)..]);
        }
        return (text, null);
    }

    /// <summary>What a bare tap of the hold key does now: retry the card's run, or copy the raw transcript the pill offers.</summary>
    public TapAction Tap() => Current switch
    {
        { State: OverlayState.Error, HistoryId: { } id } when Current.Buttons.Contains(OverlayButton.Retry) => new TapAction(TapKind.Retry, id),
        { OfferUseRaw: true, HistoryId: { } id } => new TapAction(TapKind.CopyRaw, id),
        _ => TapAction.None,
    };

    /// <summary>The preview's key hint, with the time left only in its last 10 s (a ticking number distracts a long read).</summary>
    private static string PreviewHint(long remainingMs, bool paused)
    {
        const string keys = "Enter insert \u00b7 C copy \u00b7 Esc discard";
        if (paused) return "Paused \u00b7 " + keys;
        return remainingMs <= 10_000 ? $"{Math.Max(0, (remainingMs + 999) / 1000)} s \u00b7 " + keys : keys;
    }

    private OverlayView Listening()
    {
        var elapsed = _nowMs - _startMs;
        string? detail;
        if (_capEndMs is { } capEnd) detail = $"{Math.Max(0, (capEnd - _nowMs + 999) / 1000)} s left";
        // No buffer at all after the start sound's 2 s wait: a muted mic still delivers silence, so it isn't "muted?".
        else if (_micDead) detail = "Mic not responding";
        // Nothing is recorded yet: the line says so, for whoever can't hear the start sound (or has it off).
        else if (!_micReady && elapsed >= StartingGraceMs) detail = "Mic starting\u2026";
        // Silence counts from when the mic was ready: a slow headset isn't "muted" before anyone could speak.
        else if (_micReady && _lastVoiceMs is null && _nowMs - _readyAtMs >= SilenceMs) detail = "No audio: is the mic muted?";
        else if (_contextLine is not null) detail = _contextLine;
        else if (_latched) detail = $"Tap {HoldKeyName} to stop";
        else detail = null;
        var title = _app.Length == 0 ? Label(_mode) : $"{Label(_mode)} · {ShortApp(_app)}";
        return new OverlayView(OverlayState.Listening, _mode, title, detail, Minutes(elapsed), 0, null, [], false,
            null, false, null, false, FixedWidth: true);
    }

    private OverlayView Working(string title, string? detail) =>
        new(OverlayState.Working, _mode, title, detail, Minutes(_nowMs - _startMs), 0, null, [], false, null, false, null, false, FixedWidth: true);

    private void ShowStream(long now)
    {
        _lastStreamShownMs = now;
        Current = Current with { Detail = LastLine(_stream.ToString()) };
    }

    private static string Label(Mode m) => m.ToString();

    private static string Minutes(long ms) => $"{ms / 60_000}:{ms / 1000 % 60:00}";

    /// <summary>Cut at a word, not mid-word, unless the first word alone is longer than the room.</summary>
    private static string Snip(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        if (s.Length <= SnippetChars) return s;
        var cut = s.LastIndexOf(' ', SnippetChars);
        return (cut > SnippetChars / 2 ? s[..cut] : s[..SnippetChars]).TrimEnd() + "…";
    }

    /// <summary>What happens when the click doesn't come: the reply is copied instead.</summary>
    private static string PendingLeft(long seconds) => $"{seconds} s left, then it's copied";

    private const int AppChars = 40;

    /// <summary>An app name short enough that the title stays on one line.</summary>
    private static string ShortApp(string app) => app.Length <= AppChars ? app : app[..(AppChars - 1)].TrimEnd() + "…";

    private static string LastLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var last = lines.Length == 0 ? "" : lines[^1].Trim();
        return last.Length <= 120 ? last : "…" + last[^119..];
    }
}
