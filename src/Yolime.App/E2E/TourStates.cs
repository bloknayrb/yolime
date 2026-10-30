#if YOLIME_E2E
using Yolime.Core;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Logging;
using Yolime.Core.Overlay;
using Yolime.Core.Pipeline;
using Yolime.Core.Updates;

namespace Yolime.App.E2E;

/// <summary>
/// The UI tour's overlay states (docs/superpowers/plans/2026-10-30-ui-tour.md): each a named list of the inputs the
/// coordinator would post, stamped from a base time, and the view it must produce. Staged through the real model, so
/// titles, details and buttons are the real ones; a named table rather than a script, so the critics only see states
/// the app can reach. The text is fixed fake text.
/// </summary>
internal static class TourStates
{
    /// <param name="Shows">What the view's text must say, checked in the app; a mismatch is reported without the text.</param>
    public sealed record Entry(string Name, OverlayState State, OverlayButton[] Buttons, bool UseRaw, Func<long, OverlayInput[]> Build, Func<OverlayView, bool>? Shows = null);

    private const float Voiced = 0.015f, Silent = 0.0005f;
    private const string LongApp = "Contoso Enterprise Resource Planning Client (Finance Workspace)";
    private const string Snippet = "Can you send me the final Q3 numbers before Thursday's budget meeting?";
    private const string LongSnippet = "Following up on the thread from last week about the vendor contract renewal: legal flagged two clauses in section 4, " +
        "procurement wants the pricing table redone, and finance needs the totals by Friday. Can you pull it together?";
    private const string Url = "https://contoso.sharepoint.com/sites/finance/Shared%20Documents/Forms/AllItems.aspx?id=%2Fsites%2Ffinance%2F" +
        "Shared%20Documents%2FQ3%2Dbudget%2Dreview%2Dfinal%2Dv7%2Exlsx&parent=%2Fsites%2Ffinance";
    private const string Transcript = "please send the weekly report to the whole team before noon tomorrow";
    private const string Rewritten = "Hi all, please send me the weekly report before noon tomorrow. Thanks!";
    private const string LongPreview = "Hi team,\n\nThanks for the quick turnaround on the draft. A few notes before we send it to the client: the summary " +
        "should lead with the cost savings, the timeline slide needs the revised launch date, and the appendix can drop the older survey " +
        "results. I'd also like one more pass on the pricing table so the totals match the spreadsheet.\n\nCould you have a revised version " +
        "ready by Thursday morning? I'll review it before the 2 pm call.\n\nThanks,\nB";

    private static readonly OverlayButton[] None = [];
    private static readonly OverlayButton[] PreviewButtons = [OverlayButton.Insert, OverlayButton.Copy, OverlayButton.Discard];
    private static readonly AppVersion Next = new(0, 1, 1, false), Running = new(0, 1, 0, false);

    /// <summary>Every non-card entry, in the order the tour captures them; Updating is last (nothing replaces it).</summary>
    public static IReadOnlyList<Entry> All { get; } =
    [
        Listen("listen-dictate", Mode.Dictate, "Outlook", shows: v => v.Detail is null && v.Title == "Dictate · Outlook" && v.Elapsed == "0:04"),
        Listen("listen-prompt", Mode.Prompt, "Outlook", shows: v => v.Detail is null && v.Title == "Prompt · Outlook"),
        Listen("listen-reply", Mode.Reply, "Slack", shows: v => v.Detail is null && v.Title == "Reply · Slack"),
        Listen("listen-raw", Mode.Raw, "Notepad++", shows: v => v.Detail is null && v.Title == "Raw · Notepad++"),
        Listen("listen-latched", Mode.Dictate, "Outlook", latched: true, shows: v => v.Detail == "Tap Right Ctrl to stop"),
        // A real mic not ready by 2 s posts AudioFlowing(false), so "starting" is only seen between 300 ms and 2 s.
        new("listen-mic-starting", OverlayState.Listening, None, false, t => [Rec(Mode.Dictate, "Outlook", t, micReady: false), new Tick(t + 800)],
            v => v.Detail == "Mic starting…"),
        // No Level here: any buffer means the mic is delivering, which clears "not responding".
        new("listen-mic-not-responding", OverlayState.Listening, None, false, t =>
            [Rec(Mode.Dictate, "Outlook", t, micReady: false), new AudioFlowing(t + 2000, false, Guid.Empty), new Tick(t + 2500)],
            v => v.Detail == "Mic not responding"),
        new("listen-no-audio", OverlayState.Listening, None, false, t => [Rec(Mode.Dictate, "Outlook", t), new Level(Silent, t + 500), new Tick(t + 3000)],
            v => v.Detail == "No audio: is the mic muted?"),
        // A held recording's cap is 2 minutes; the countdown starts 10 s before it.
        new("listen-cap-countdown", OverlayState.Listening, None, false, t =>
            [Rec(Mode.Dictate, "Outlook", t), new CapCountdown(10, t + 110_000), new Tick(t + 112_000), new Level(Voiced, t + 111_900)],
            v => v.Detail == "8 s left" && v.Elapsed == "1:52"),
        Listen("reply-selection", Mode.Reply, "Slack", new ContextShown(Snippet, ContextSource.Selection, 0, false),
            shows: v => v.Detail?.StartsWith("Replying to: '", StringComparison.Ordinal) == true && v.Detail.EndsWith("(selected text)", StringComparison.Ordinal)),
        Listen("reply-clipboard", Mode.Reply, "Slack", new ContextShown(Snippet, ContextSource.Clipboard, 42_000, false),
            shows: v => v.Detail?.EndsWith("(copied 42 s ago)", StringComparison.Ordinal) == true),
        Listen("reply-skipped", Mode.Reply, "Slack", new ContextShown(null, null, 0, true), shows: v => v.Detail == "Copied text skipped (looks like a password or key)"),
        Listen("reply-none", Mode.Reply, "Slack", new ContextShown(null, null, 0, false), shows: v => v.Detail == "No context"),
        new("work-transcribing", OverlayState.Working, None, false, t =>
            [Rec(Mode.Dictate, "Outlook", t), new Level(Voiced, t + 100), new RecordingEnded(t + 5000), new Tick(t + 6000)],
            v => v.Title == "Transcribing…" && v.Detail is null && v.Elapsed == "0:06"),
        new("work-transcript", OverlayState.Working, None, false, t =>
            [Rec(Mode.Dictate, "Outlook", t), new Level(Voiced, t + 100), new RecordingEnded(t + 5000), new TranscriptShown(Transcript), new Tick(t + 6000)],
            v => v.Title == "Transcribed" && v.Detail == Transcript),
        Writing("work-writing", "Outlook", "Hi all, please send me ", "the weekly report before noon tomorrow."),
        Done("done-pasted", Mode.Dictate, "Outlook", DeliveryKind.Pasted, rewritten: false),
        Done("done-copied", Mode.Dictate, "Outlook", DeliveryKind.Copied, rewritten: false),
        Done("done-use-raw", Mode.Prompt, "Outlook", DeliveryKind.Pasted, rewritten: true),
        Preview("preview-short", Rewritten),
        Preview("preview-long", LongPreview),
        new("pending-paste", OverlayState.PendingPaste, None, false, t =>
            [Rec(Mode.Reply, "Slack", t), new PendingPasteStarted(8, t + 1000), new Tick(t + 3000)], v => v.Detail == "6 s left, then it's copied"),
        Listen("long-listen", Mode.Prompt, LongApp, shows: v => v.Title == "Prompt · " + Short(LongApp)),
        Listen("long-listen-context", Mode.Reply, LongApp, new ContextShown(LongSnippet, ContextSource.Selection, 0, false),
            shows: v => v.Title == "Reply · " + Short(LongApp) && v.Detail?.StartsWith("Replying to: '", StringComparison.Ordinal) == true),
        Writing("long-writing", LongApp, "Here is the link: ", Url),
        Done("long-done", Mode.Prompt, LongApp, DeliveryKind.Pasted, rewritten: true),
        Preview("long-preview-url", Url),
        new("card-update-failed", OverlayState.Error, [OverlayButton.OpenLog, OverlayButton.Dismiss], false, t =>
            [new UpdatingShown(Next, t), new UpdatingEnded(new Notice(NoticeKind.UpdateFailed, Version: Next, Current: Running, UpdateFailure: UpdateFailure.SetupFailed), t + 1000)]),
        new("card-update-slow", OverlayState.Error, [OverlayButton.OpenLog, OverlayButton.Dismiss], false, t =>
            [new UpdatingShown(Next, t), new UpdatingEnded(new Notice(NoticeKind.UpdateSlow, Version: Next), t + 300_000)]),
        new("card-update-hold-refused", OverlayState.Error, [OverlayButton.Dismiss], false, t => [new NoticeOnly(new Notice(NoticeKind.UpdateHoldRefused), t)]),
        new("updating", OverlayState.Updating, None, false, t => [new UpdatingShown(Next, t)]),
        // Not captured: the reset alone, for the capture-hiding control's "nothing showing" image and before the windows.
        new("hidden", OverlayState.Hidden, None, false, _ => []),
        // Not captured: negative controls. Each must come back "mismatch", or the state check proves nothing.
        new("control-wrong-state", OverlayState.Done, None, false, t => [Rec(Mode.Dictate, "Outlook", t), new Tick(t + 4200), new Level(Voiced, t + 4100)]),
        new("control-wrong-text", OverlayState.Listening, None, false, t => [Rec(Mode.Dictate, "Outlook", t), new Tick(t + 4200), new Level(Voiced, t + 4100)],
            v => v.Title == "Prompt · Outlook"),
    ];

    /// <summary>The notices that only ever arrive outside a run (<see cref="NoticeOnly"/>), as the coordinator posts them.</summary>
    private static readonly HashSet<NoticeKind> OutsideARun =
    [
        NoticeKind.Busy, NoticeKind.Discarded, NoticeKind.CapReached, NoticeKind.PasswordRefused, NoticeKind.RetryUnavailable,
        NoticeKind.PasteLastUnavailable, NoticeKind.Copied, NoticeKind.CopiedAdminWindow, NoticeKind.PreviewKept, NoticeKind.RawCopied, NoticeKind.MicFallback,
    ];

    /// <summary>A named entry, or a card from a show-card spec ("card Kind Mode|- Copied Retryable"); null if neither.</summary>
    public static Entry? Find(string name)
    {
        if (All.FirstOrDefault(e => e.Name == name) is { } entry) return entry;
        if (!name.StartsWith("card ", StringComparison.Ordinal)) return null;
        var parts = name[5..].Split(' ');
        if (parts.Length != 4 || !Enum.TryParse<NoticeKind>(parts[0], out var kind)) return null;
        Mode? mode = parts[1] == "-" ? null : Enum.Parse<Mode>(parts[1]);
        var copied = bool.Parse(parts[2]);
        var retryableSpec = bool.Parse(parts[3]);
        var notice = new Notice(kind, mode, Copied: copied, Retryable: retryableSpec);
        if (OutsideARun.Contains(kind))
            return new(name, OverlayState.Error, [OverlayButton.Dismiss], false, t => [new NoticeOnly(notice, t)]);
        var retryable = bool.Parse(parts[3]);
        // Key and settings problems open Settings instead of Retry (the model's rule, proved in Core).
        var settings = Notices.SettingsPage(kind) is not null;
        OverlayButton[] buttons = [.. retryable && !settings ? [OverlayButton.Retry] : None, .. kind == NoticeKind.MicDenied ? [OverlayButton.OpenSettings] : None,
            .. settings ? [OverlayButton.OpenYolimeSettings] : None, OverlayButton.Dismiss];
        // NoSpeech with a history id is retryable, without one it isn't: the model's Retryable() decides, as show-card does.
        return new(name, OverlayState.Error, buttons, false, t =>
            [new RunFinished(new RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.NoSpeech, Mode = mode ?? Mode.Dictate, HistoryId = retryable ? 1 : null }, notice, "Outlook", t)]);
    }

    /// <summary>What's on screen, when it differs from what the entry must show; null when it matches.</summary>
    public static string? Mismatch(Entry e, OverlayView v) =>
        !(v.State == e.State && v.Buttons.SequenceEqual(e.Buttons) && v.OfferUseRaw == e.UseRaw)
            ? $"expected {e.State} [{string.Join(",", e.Buttons)}] useRaw={e.UseRaw}, got {v.State} [{string.Join(",", v.Buttons)}] useRaw={v.OfferUseRaw}"
            // The text itself stays in the app: only that it differs, and its lengths.
            : e.Shows is { } shows && !shows(v) ? $"{v.State} text differs (title {v.Title.Length}, detail {v.Detail?.Length ?? 0} chars)"
            : null;

    /// <summary>The model's rule for app names in titles: 40 characters at most.</summary>
    private static string Short(string app) => app.Length <= 40 ? app : app[..39].TrimEnd() + "…";

    private static RecordingStarted Rec(Mode mode, string app, long t, bool latched = false, bool micReady = true) => new(mode, app, latched, t, Guid.Empty, micReady);

    /// <summary>Listening with a 0:04 clock; the voiced level goes last (context and the clock rebuild the view without it).</summary>
    private static Entry Listen(string name, Mode mode, string app, ContextShown? context = null, bool latched = false, Func<OverlayView, bool>? shows = null) =>
        new(name, OverlayState.Listening, None, false, t => context is null
            ? [Rec(mode, app, t, latched), new Tick(t + 4200), new Level(Voiced, t + 4100)]
            : [Rec(mode, app, t, latched), context, new Tick(t + 4200), new Level(Voiced, t + 4100)], shows);

    /// <summary>Writing for the app, with two deltas 300 ms apart so the stream throttle shows them.</summary>
    private static Entry Writing(string name, string app, string first, string second) =>
        new(name, OverlayState.Working, None, false, t =>
            [Rec(Mode.Prompt, app, t), new Level(Voiced, t + 100), new RecordingEnded(t + 5000), new TranscriptShown(Transcript),
             new StageBegan(Stage.Rewrite, t + 6000), new DeltaArrived(first, t + 6100), new DeltaArrived(second, t + 6400), new Tick(t + 6500)],
            // A line over 120 characters shows its end, after an ellipsis.
            v => v.Title == $"Writing for {Short(app)}…" && v.Detail is { } d && (d.StartsWith(first.Trim(), StringComparison.Ordinal) || d.StartsWith('…')));

    /// <summary>A finished run with its word count and time; a rewritten paste with a history id offers Use raw.</summary>
    private static Entry Done(string name, Mode mode, string app, DeliveryKind delivery, bool rewritten) =>
        new(name, OverlayState.Done, None, rewritten && delivery == DeliveryKind.Pasted, t =>
        {
            var run = Guid.NewGuid();
            var timing = new RunTiming { RunId = run, Mode = mode, StartedUtc = DateTimeOffset.UtcNow, Outcome = RunOutcome.Completed, OutputWords = 14, OutputDoneMs = 1840 };
            var result = new RunResult { RunId = run, Outcome = RunOutcome.Completed, Mode = mode, Delivery = delivery, Rewritten = rewritten, HistoryId = 7, Timing = timing };
            return [Rec(mode, app, t), new Level(Voiced, t + 100), new RecordingEnded(t + 4000), new RunFinished(result, null, app, t + 5800)];
        }, v => delivery == DeliveryKind.Copied ? v.Title == "Copied: press\u00a0Ctrl+V" : v.Title == $"→ {Short(app)} · 14 words · 1.8 s");

    private static Entry Preview(string name, string text) =>
        new(name, OverlayState.Preview, PreviewButtons, false, t => [Rec(Mode.Raw, "Outlook", t), new Level(Voiced, t + 100), new PreviewShown(text)],
            v => v.Preview == text && v.Title == "Preview · Raw");
}
#endif
