using Yolime.Core.History;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Overlay;

/// <summary>A page of yolime's Settings that a notice can point at (the App maps it to the window's own page names).</summary>
public enum SettingsPage { General, Keys, Advanced }

/// <summary>What a bare tap of the hold key does to what's on the notch.</summary>
public enum TapKind { None, Retry, CopyRaw }

/// <param name="HistoryId">The run the tap acts on.</param>
public sealed record TapAction(TapKind Kind, long? HistoryId = null)
{
    public static readonly TapAction None = new(TapKind.None);
}

/// <summary>
/// Which finished runs can be tried again: the same set the tray's Retry last failed accepts (a kept recording, or a
/// Prompt/Reply whose rewrite failed and fell back). One rule, used by the card's Retry button and by the notices that
/// tell the user where to retry.
/// </summary>
public static class RunRetry
{
    public static bool IsRetryable(RunResult r) => r.HistoryId is not null && (r.Outcome is RunOutcome.SttFailed or RunOutcome.NoSpeech
        or RunOutcome.Interrupted or RunOutcome.Cancelled or RunOutcome.CaptureFailed
        || (r.Outcome == RunOutcome.CompletedWithFallback && r.RewriteError is not null && r.Mode is Mode.Prompt or Mode.Reply));
}

/// <summary>
/// The notch's button labels. A button names its key when one works: Retry and Copy raw by a tap of the hold key, which
/// never reaches the notch while double-tap-to-latch is on (the tap might be the first of a latch), so then they don't.
/// </summary>
public static class ButtonLabels
{
    public static string For(OverlayButton button, string holdKey, bool tapReaches) => button switch
    {
        OverlayButton.Retry => tapReaches ? $"Retry (tap {holdKey})" : "Retry",
        OverlayButton.OpenSettings => "Open privacy settings",
        OverlayButton.OpenYolimeSettings => "Open yolime Settings",
        OverlayButton.OpenLog => "Open the log",
        OverlayButton.Insert => "Insert (Enter)",
        OverlayButton.Copy => "Copy (C)",
        OverlayButton.Discard => "Discard (Esc)",
        _ => "Dismiss (Esc)",
    };

    /// <summary>The pill beside Done: it copies the raw transcript (it isn't an <see cref="OverlayButton"/>).</summary>
    public static string Pill(string holdKey, bool tapReaches) => tapReaches ? $"Copy raw (tap {holdKey})" : "Copy raw";
}
