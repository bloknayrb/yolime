using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;
using Yolime.Core.Stt;
using Yolime.Core.Updates;

namespace Yolime.Core.Feedback;

public enum NoticeKind
{
    Started,
    AlreadyRunning,
    NoKey,
    KeyRejected,
    OutOfCredit,
    RateLimited,
    ServiceUnreachable,
    EndpointInvalid,
    TranscriptionFailed,
    MicDenied,
    NoMic,
    MicLost,
    DidntHear,
    Busy,
    Copied,
    CopiedAdminWindow,
    PasswordRefused,
    OutputFailed,
    Interrupted,
    Discarded,
    CapWarning,
    CapReached,
    Cancelled,
    RetryUnavailable,
    RewriteNoKey,
    RewriteKeyRejected,
    RewriteOutOfCredit,
    RewriteRefused,
    RewriteTooLong,
    RewriteTimedOut,
    RewriteUnavailable,
    RewriteConfig,
    RewriteEmpty,
    ContextSkipped,
    PreviewKept,
    RawCopied,
    HoldKeyReset,
    PasteLastUnavailable,
    ElevatedTarget,
    MicFallback,
    SettingsNotSaved,
    UpdateAvailable,
    UpdateAvailableElevated,
    Updated,
    UpdateFailed,
    /// <summary>Setup ran five minutes without asking yolime to exit: holds are back, the card stays until dismissed.</summary>
    UpdateSlow,
    /// <summary>A hold while an update installs.</summary>
    UpdateHoldRefused,
    /// <summary>The download, its check or setup's start failed while the update window was closed.</summary>
    UpdateAttemptFailed,
}

/// <summary>
/// Something to tell the user. Enum-only by design, like log fields: no transcript or other content can reach a
/// balloon (which Windows keeps in the Action Center).
/// </summary>
/// <param name="Copied">For rewrite notices: the text went to the clipboard rather than being pasted.</param>
/// <param name="HoldKey">For Started: the hold key's virtual-key code, named from a fixed list.</param>
/// <param name="Version">For update notices: the version offered or installed.</param>
/// <param name="Current">For a failed update: the version still running.</param>
/// <param name="UpdateFailure">For a failed update: why, from the result word's fixed set (recorded, not shown).</param>
/// <param name="Problem">For an install attempt that stopped before setup ran: which step.</param>
/// <param name="NotKept">For a failed run: nothing was kept to retry (history is paused), so the text mustn't offer Retry.</param>
public sealed record Notice(NoticeKind Kind, Mode? Mode = null, int? Seconds = null, bool Copied = false, int? HoldKey = null,
    AppVersion? Version = null, AppVersion? Current = null, UpdateFailure? UpdateFailure = null, UpdateProblem? Problem = null, bool NotKept = false);

public static class Notices
{
    /// <summary>
    /// The notice for a finished run, or null when silence is right (a paste, a discarded recording, a busy press).
    /// A cancel says so only when history kept the run; one kept as a WAV alone, with history unavailable, stays
    /// silent and is surfaced by Housekeeping on the next start.
    /// </summary>
    // Only a paused history keeps nothing. A row that failed to save leaves its recording on disk, and Housekeeping
    // gives it a row (so a Retry) on the next start.
    public static Notice? For(RunResult r) => Base(r) is { } n && r.HistoryPaused ? n with { NotKept = true } : Base(r);

    private static Notice? Base(RunResult r) => r.Outcome switch
    {
        RunOutcome.NoSpeech => new(NoticeKind.DidntHear),
        RunOutcome.SttFailed => new(r.SttError switch
        {
            SttError.MissingKey => NoticeKind.NoKey,
            SttError.Auth => NoticeKind.KeyRejected,
            SttError.QuotaExceeded => NoticeKind.OutOfCredit,
            SttError.RateLimited => NoticeKind.RateLimited,
            SttError.Network or SttError.Timeout or SttError.Server => NoticeKind.ServiceUnreachable,
            SttError.InsecureEndpoint or SttError.InvalidEndpoint => NoticeKind.EndpointInvalid,
            _ => NoticeKind.TranscriptionFailed,
        }),
        RunOutcome.CaptureFailed => new(r.CaptureError switch
        {
            CaptureError.AccessDenied => NoticeKind.MicDenied,
            CaptureError.NoDevice => NoticeKind.NoMic,
            _ => NoticeKind.MicLost,
        }),
        RunOutcome.OutputFailed => new(NoticeKind.OutputFailed),
        RunOutcome.Interrupted => new(NoticeKind.Interrupted),
        // Only when something was kept: a discard while recording (Esc, a short tap) stays silent.
        RunOutcome.Cancelled when r.HistoryId is not null => new(NoticeKind.Cancelled),
        // The rewrite failed and the run fell back: say why, and (through Mode) where the transcript went.
        RunOutcome.CompletedWithFallback when r.RewriteError is { } f => new(RewriteKind(f), r.Mode, Copied: r.Delivery != DeliveryKind.Pasted),
        // A retry from the transcript couldn't start: no Mode, so the copy says nothing was changed.
        RunOutcome.RewriteFailed when r.RewriteError is { } f => new(RewriteKind(f)),
        _ when r.ContextNote == ContextNote.Skipped => new(NoticeKind.ContextSkipped),
        // Copied and refused deliveries were announced by the output step itself.
        _ => null,
    };

    private static NoticeKind RewriteKind(RewriteFailure f) => f switch
    {
        RewriteFailure.MissingKey => NoticeKind.RewriteNoKey,
        RewriteFailure.Auth => NoticeKind.RewriteKeyRejected,
        RewriteFailure.Billing => NoticeKind.RewriteOutOfCredit,
        RewriteFailure.Refusal => NoticeKind.RewriteRefused,
        RewriteFailure.MaxTokens => NoticeKind.RewriteTooLong,
        RewriteFailure.Timeout => NoticeKind.RewriteTimedOut,
        RewriteFailure.Config => NoticeKind.RewriteConfig,
        RewriteFailure.EmptyOutput => NoticeKind.RewriteEmpty,
        _ => NoticeKind.RewriteUnavailable,
    };

    /// <summary>The notice for how a result was delivered.</summary>
    public static Notice? For(OutputNotice n) => n switch
    {
        OutputNotice.None => null,
        OutputNotice.Refused => new(NoticeKind.PasswordRefused),
        OutputNotice.CopiedElevated => new(NoticeKind.CopiedAdminWindow),
        _ => new(NoticeKind.Copied),
    };

    /// <summary>
    /// The notice's text. On the overlay card the Retry and settings actions are buttons, so the "from the tray"
    /// directions are dropped there.
    /// </summary>
    public static string Text(Notice n, bool onCard) => !onCard ? Text(n) : Text(n)
        .Replace("use Retry last failed in the tray", "Retry", StringComparison.Ordinal)
        .Replace(" from the tray", "", StringComparison.Ordinal)
        .Replace("Click to open Windows privacy settings.", "Turn it on in Windows privacy settings.", StringComparison.Ordinal)
        // The card has an "Open the log" button instead.
        .Replace(", so yolime is still on", "; yolime is still on", StringComparison.Ordinal)
        .Replace(" Click to open the log.", "", StringComparison.Ordinal);

    /// <summary>The balloon's icon: info for good news and confirmations, warning for anything that needs the user.</summary>
    public static bool IsInfo(NoticeKind kind) =>
        kind is NoticeKind.Started or NoticeKind.Copied or NoticeKind.Cancelled or NoticeKind.RawCopied or NoticeKind.UpdateAvailable or NoticeKind.Updated;

    public static string Text(Notice n) => n.Kind switch
    {
        NoticeKind.Started => $"yolime is running. Hold {Hotkeys.HoldKeyRules.Name(n.HoldKey ?? Hotkeys.HoldKeyRules.RightCtrl)}, speak, and let go.",
        NoticeKind.SettingsNotSaved => "yolime couldn't save that change: settings.json is read-only or from a newer version.",
        NoticeKind.MicFallback => "Your chosen microphone isn't connected, so yolime is using the default one.",
        NoticeKind.ElevatedTarget => "That window runs as administrator, so yolime can't paste into it. Results there are copied for you to paste.",
        NoticeKind.AlreadyRunning => "yolime is already running.",
        NoticeKind.NoKey => "No transcription key yet. Add one in Settings, under Keys.",
        NoticeKind.KeyRejected => "The transcription service rejected the API key. Check it in Settings, under Keys.",
        NoticeKind.OutOfCredit => Kept("The transcription account is out of credit.", n),
        NoticeKind.RateLimited => Kept("The transcription service is busy.", n, "Your recording is kept: Retry from the tray in a moment."),
        NoticeKind.ServiceUnreachable => Kept("Couldn't reach the transcription service.", n),
        NoticeKind.EndpointInvalid => "The custom transcription address isn't valid (it must be https). Fix it in settings.json (Settings, under Advanced).",
        NoticeKind.TranscriptionFailed => Kept("Transcription failed.", n),
        NoticeKind.MicDenied => "Microphone access is off. Click to open Windows privacy settings.",
        NoticeKind.NoMic => "No microphone found.",
        NoticeKind.MicLost => Kept("The microphone disconnected.", n, "If anything was recorded, Retry from the tray."),
        NoticeKind.DidntHear => Kept("Didn't hear anything. Is the mic muted?", n, "Retry from the tray to send it anyway."),
        NoticeKind.Busy => $"Still working on your {Noun(n.Mode)}. Tap the hold key to cancel it. This recording wasn't kept.",
        NoticeKind.Copied => "Copied. Press Ctrl+V to paste.",
        NoticeKind.CopiedAdminWindow => "yolime can't type into admin windows. The text is copied: press Ctrl+V.",
        NoticeKind.PasswordRefused => "That's a password field: nothing was recorded or typed.",
        NoticeKind.OutputFailed => n.NotKept ? "Couldn't paste or copy the result, and history is paused, so it wasn't kept." : "Couldn't paste or copy the result. It's in history.",
        NoticeKind.Interrupted => Kept("Something went wrong.", n),
        NoticeKind.Discarded => "Recording discarded: you pressed another key or switched windows.",
        NoticeKind.CapWarning => $"{n.Seconds ?? 10} seconds left in this recording.",
        NoticeKind.CapReached => "Reached the recording limit: it's being transcribed now.",
        NoticeKind.Cancelled => "Cancelled. Your recording is kept: Retry from the tray if you want it after all.",
        NoticeKind.RetryUnavailable => "There's nothing to retry.",
        NoticeKind.PasteLastUnavailable => "There's nothing to paste again. Results are kept for 30 minutes, and not while history is paused.",
        NoticeKind.RewriteNoKey => Rewrite("No Claude key yet. Add one in Settings, under Keys.", n),
        NoticeKind.RewriteKeyRejected => Rewrite("Claude rejected the API key.", n),
        NoticeKind.RewriteOutOfCredit => Rewrite("The Claude account is out of credit.", n),
        NoticeKind.RewriteRefused => Rewrite("Claude declined to rewrite this.", n),
        NoticeKind.RewriteTooLong => Rewrite("The rewrite ran too long and was cut off.", n),
        NoticeKind.RewriteTimedOut => Rewrite("Claude took too long to answer.", n),
        NoticeKind.RewriteUnavailable => Rewrite("Couldn't reach Claude.", n),
        NoticeKind.RewriteConfig => Rewrite("Claude rejected the request: check the Prompt model in Settings, under Advanced.", n),
        NoticeKind.RewriteEmpty => Rewrite("Claude returned nothing.", n),
        NoticeKind.HoldKeyReset => "Your hold key isn't supported any more, so it is Right Ctrl now. Pick another in Settings, under General.",
        NoticeKind.RawCopied => "Raw transcript copied: press Ctrl+V.",
        NoticeKind.PreviewKept => "Preview closed: nothing was inserted. The text is kept in history.",
        NoticeKind.ContextSkipped => "The copied text looked like it held a password or key, so it wasn't sent.",
        NoticeKind.UpdateAvailable => $"yolime {Ver(n.Version)} is available. Click to install, or use the tray menu.",
        NoticeKind.UpdateAvailableElevated => $"yolime {Ver(n.Version)} is available, but this copy runs as administrator, so it can't install it. Exit and start yolime from the Start menu.",
        NoticeKind.Updated => $"Updated to {Ver(n.Version)}.",
        NoticeKind.UpdateFailed => $"The update to {Ver(n.Version)} didn't install, so yolime is still on {n.Current?.ToString() ?? "the version you had"}. Click to open the log.",
        NoticeKind.UpdateSlow => $"The update to {Ver(n.Version)} is taking longer than it should. You can use yolime again; if it doesn't finish, open the log.",
        NoticeKind.UpdateHoldRefused => "yolime is installing an update, so dictation is off for a moment.",
        NoticeKind.UpdateAttemptFailed => n.Problem switch
        {
            UpdateProblem.ChecksumMismatch => $"The yolime {Ver(n.Version)} download didn't match its checksum, so it wasn't installed. Click to try again.",
            UpdateProblem.LaunchFailed => $"Couldn't start the yolime {Ver(n.Version)} setup. Nothing changed. Click to try again.",
            _ => $"Couldn't download yolime {Ver(n.Version)}. Nothing changed. Click to try again.",
        },
        _ => n.Kind.ToString(),
    };

    /// <summary>Why, then where the text went: Dictate pasted the raw transcript, Prompt and Reply copied it.</summary>
    private static string Rewrite(string why, Notice n) => n.Mode switch
    {
        null => $"{why} Nothing was changed.",
        Mode.Dictate when !n.Copied => $"{why} The raw transcript was pasted instead.",
        Mode.Dictate => $"{why} The raw transcript is copied: press Ctrl+V.",
        _ when n.NotKept => $"{why} The raw transcript is copied: press Ctrl+V.",
        _ => $"{why} The raw transcript is copied: press Ctrl+V, or use Retry last failed in the tray.",
    };

    /// <summary>Why, then the retry it offers: only when something was kept to retry.</summary>
    private static string Kept(string why, Notice n, string retry = "Your recording is kept: Retry from the tray.") =>
        n.NotKept ? $"{why} History is paused, so the recording wasn't kept." : $"{why} {retry}";

    private static string Ver(AppVersion? v) => v?.ToString() ?? "a new version";

    private static string Noun(Mode? mode) => mode switch
    {
        Mode.Prompt => "prompt",
        Mode.Reply => "reply",
        Mode.Raw => "transcript",
        _ => "dictation",
    };
}

/// <summary>Shows any one notice at most once per window, so a stuck condition can't flood the user.</summary>
public sealed class NoticeLimiter(TimeSpan window)
{
    private readonly Dictionary<NoticeKind, long> _lastShownMs = [];

    public bool ShouldShow(Notice n, long nowMs)
    {
        lock (_lastShownMs)
        {
            if (_lastShownMs.TryGetValue(n.Kind, out var last) && nowMs - last < window.TotalMilliseconds) return false;
            _lastShownMs[n.Kind] = nowMs;
            return true;
        }
    }
}
