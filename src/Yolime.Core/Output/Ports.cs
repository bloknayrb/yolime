using Yolime.Core.Feedback;

namespace Yolime.Core.Output;

/// <summary>The user's clipboard as it was, as raw bytes per format. Wiped once restored or abandoned.</summary>
public sealed class SavedClipboard(IReadOnlyList<(uint Format, byte[] Data)> items, bool isSensitive, bool hadContent)
{
    public IReadOnlyList<(uint Format, byte[] Data)> Items { get; } = items;

    /// <summary>Marked by a password manager: never put back (its auto-clear may already have been missed).</summary>
    public bool IsSensitive { get; } = isSensitive;

    /// <summary>The clipboard wasn't empty, even if nothing in it could be saved.</summary>
    public bool HadContent { get; } = hadContent;

    public void Wipe()
    {
        foreach (var (_, data) in Items) Array.Clear(data);
    }
}

/// <summary>
/// A set failed after the clipboard was already emptied or partly written; <see cref="Sequence"/> identifies that
/// state, so the caller can still put the user's content back.
/// </summary>
public sealed class ClipboardChangedException(uint sequence, Exception inner)
    : IOException("The clipboard was changed but not fully set.", inner)
{
    public uint Sequence { get; } = sequence;
}

/// <summary>What the user chose on the preview card. <see cref="Expired"/>: it ended any other way (another key, a
/// click elsewhere, a window switch, the timeout), which keeps the text in history and touches nothing.</summary>
public enum PreviewChoice { Insert, Copy, Discard, Expired }

/// <summary>Shows the preview card and waits for a choice. Never steals focus: keys are captured by the hook.</summary>
public interface IPreviewPrompt
{
    Task<PreviewChoice> AskAsync(string text, CancellationToken ct);
}

/// <summary>
/// Waits for the user to click into an editable field of <c>processId</c>, for a Reply whose selection was read-only.
/// Only a focus change that follows the user's own click counts. Returns the new target, or null when time runs out.
/// </summary>
public interface IEditableFocusWaiter
{
    Task<TargetSnapshot?> WaitAsync(int processId, TimeSpan limit, CancellationToken ct);
}

public interface IClipboard
{
    Task<SavedClipboard> SaveAsync(CancellationToken ct);

    /// <returns>The clipboard sequence number after the set, to recognise our content later.</returns>
    Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct);

    /// <summary>Puts <paramref name="saved"/> back, checking under the clipboard lock that the content is still ours.</summary>
    /// <returns>The sequence number after the restore, or 0 when it wasn't ours any more and nothing changed.</returns>
    Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct);

    /// <summary>Empties the clipboard if it still holds our content.</summary>
    /// <returns>The sequence number after the clear, or 0 when nothing changed.</returns>
    Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct);

    /// <summary>
    /// Puts the transient paste copy on the clipboard as "available, rendered on request", so the read itself tells
    /// yolime when the target has its paste. One that can't offer writes it outright, as <see cref="SetTextAsync"/> does.
    /// </summary>
    Task<uint> OfferPasteAsync(string text, CancellationToken ct) => SetTextAsync(text, excludeFromHistory: true, ct);

    /// <summary>
    /// Waits, up to <paramref name="max"/>, for the first read of the offered paste. The read is latched at the offer,
    /// so one that came before this call is returned at once.
    /// </summary>
    Task<PasteRead> WaitUntilReadAsync(uint ourSequence, TimeSpan max, CancellationToken ct) =>
        Task.FromResult(new PasteRead(PasteReadOutcome.NotOffered, ourSequence));
}

public enum PasteReadOutcome
{
    /// <summary>The paste was written outright, so there is no read to wait for.</summary>
    NotOffered,
    Read,
    /// <summary>Someone else emptied the clipboard (a new copy): the content isn't ours to restore.</summary>
    Emptied,
    TimedOut,
}

/// <summary>Who read the paste, told apart by the process holding the clipboard open during the render.</summary>
public enum PasteReader { Unknown, Target, Other }

/// <param name="Sequence">Our content's sequence number now, which a render may have moved.</param>
/// <param name="ReadAfterOffer">From the offer to the read, for timing logs.</param>
public readonly record struct PasteRead(
    PasteReadOutcome Outcome,
    uint Sequence,
    PasteReader Reader = PasteReader.Unknown,
    TimeSpan? ReadAfterOffer = null);

public interface IInjector
{
    /// <summary>Waits until no modifier is physically held; throws <see cref="TimeoutException"/> after a few seconds.</summary>
    Task WaitForModifiersUpAsync(CancellationToken ct);

    /// <returns>False if the input was blocked.</returns>
    bool Paste(PasteChord chord);
}

public interface ITargetReader
{
    TargetSnapshot Snapshot();

    nint Foreground();

    /// <summary>Waits (briefly) until the target has processed its input queue, so it has read the clipboard.</summary>
    Task WaitForIdleAsync(nint hwnd, CancellationToken ct);
}

public interface INoticeSink
{
    void Show(Notice notice);
}
