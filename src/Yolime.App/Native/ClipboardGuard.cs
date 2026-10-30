using System.Runtime.InteropServices;
using Yolime.Core.Clipboard;
using Yolime.Core.Logging;
using Yolime.Core.Output;

namespace Yolime.App.Native;

/// <summary>
/// The real clipboard, following <see cref="ClipboardPolicy"/>. Every open, read and write happens in one
/// synchronous block on one thread, and restore decisions are re-checked while the clipboard is held.
/// </summary>
internal sealed class ClipboardGuard(nint owner, ILog? log = null) : IClipboard, ClipboardOwnerThread.IHandler
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint PmQsSendMessage = 0x0040 << 16;
    // Very large clipboards (video frames, huge files lists) aren't worth holding in memory to restore.
    private const long MaxSaveBytes = 64L * 1024 * 1024;
    // Reply context is capped at 8,000 characters anyway; reading more of a huge copy only costs memory.
    private const long MaxContextChars = Yolime.Core.Llm.PromptBuilder.ContextCap + 1;

    /// <summary>
    /// A paste offered by delayed rendering: the text is handed over only when an app asks for it (WM_RENDERFORMAT),
    /// which tells the restore the paste has been read. The first read is latched here, so one that comes before the
    /// restore starts waiting isn't missed.
    /// </summary>
    private sealed class Offer(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;
        public long OfferedAt { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        // Filled in after the close; only the restore's wait reads it, and that starts later.
        public volatile uint Sequence;
        // Written on the owner thread, read on the pool by the restore's ownership check.
        public volatile bool Rendered;
        public volatile uint RenderedSequence;
        public TaskCompletionSource<PasteRead> Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _offerGate = new();
    private Offer? _offer;
    // Set only while yolime holds the clipboard open and empties it, so the WM_DESTROYCLIPBOARD that empty sends is
    // known to be ours. Nobody else can empty it while we hold it.
    private volatile bool _ownEmpty;

    /// <summary>The clipboard's text for Reply context, its sequence number, and whether a password manager marked it.</summary>
    public async Task<(string? Text, uint Sequence, bool Sensitive)> ReadTextAsync(CancellationToken ct)
    {
        await OpenAsync(ct);
        try
        {
            var present = new List<ClipFormat>();
            for (var f = User32.EnumClipboardFormats(0); f != 0; f = User32.EnumClipboardFormats(f)) present.Add(new ClipFormat(f, NameOf(f)));
            var sequence = User32.GetClipboardSequenceNumber();
            if (ClipboardPolicy.IsSensitive(present)) return (null, sequence, true);
            var handle = User32.GetClipboardData(CfUnicodeText);
            if (handle == 0) return (null, sequence, false);
            // Another app wrote this block: trust its size, not a terminator it may have left out.
            var chars = (int)Math.Min((long)User32.GlobalSize(handle) / 2, MaxContextChars);
            if (chars == 0) return (null, sequence, false);
            var ptr = User32.GlobalLock(handle);
            if (ptr == 0) return (null, sequence, false);
            try
            {
                var text = Marshal.PtrToStringUni(ptr, chars);
                var nul = text.IndexOf('\0');
                return (nul >= 0 ? text[..nul] : text, sequence, false);
            }
            finally { User32.GlobalUnlock(handle); }
        }
        finally { User32.CloseClipboard(); }
    }

    public async Task<SavedClipboard> SaveAsync(CancellationToken ct)
    {
        await OpenAsync(ct);
        try
        {
            var present = new List<ClipFormat>();
            for (var f = User32.EnumClipboardFormats(0); f != 0; f = User32.EnumClipboardFormats(f)) present.Add(new ClipFormat(f, NameOf(f)));
            var items = new List<(uint, byte[])>();
            long total = 0;
            foreach (var format in ClipboardPolicy.Saveable(present))
            {
                var handle = User32.GetClipboardData(format.Id);
                if (handle == 0) continue; // delayed rendering that failed or timed out: skip it
                var size = (long)User32.GlobalSize(handle);
                if ((total += size) > MaxSaveBytes)
                {
                    // Restoring only some formats would silently change what the user copied (HTML without its image).
                    // Save nothing instead: the executor then leaves the dictated text rather than a partial clipboard.
                    foreach (var (_, saved) in items) Array.Clear(saved);
                    items.Clear();
                    break;
                }
                var ptr = User32.GlobalLock(handle);
                if (ptr == 0) continue;
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(ptr, data, 0, (int)size);
                    items.Add((format.Id, data));
                }
                finally { User32.GlobalUnlock(handle); }
            }
            return new SavedClipboard(items, ClipboardPolicy.IsSensitive(present), present.Count > 0);
        }
        finally { User32.CloseClipboard(); }
    }

    public async Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct)
    {
        await OpenAsync(ct);
        var emptied = false;
        Exception? failure = null;
        try
        {
            // A failed empty leaves the user's content as it was: report that as a plain failure, never as "ours".
            if (!EmptyOwn()) throw new IOException("EmptyClipboard failed");
            emptied = true;
            SetBytes(CfUnicodeText, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            SetMarkers(ClipboardPolicy.ExclusionFormats(excludeFromHistory));
        }
        catch (Exception ex) when (emptied)
        {
            failure = ex;
        }
        finally { User32.CloseClipboard(); }
        var sequence = User32.GetClipboardSequenceNumber();
        // The user's content is already gone: say which state is ours, so the caller can put theirs back.
        if (failure is not null) throw new ClipboardChangedException(sequence, failure);
        return sequence;
    }

    public async Task<uint> OfferPasteAsync(string text, CancellationToken ct)
    {
        // Without an owner window (the E2E sink) there is nobody to render it: write it outright.
        if (owner == 0) return await SetTextAsync(text, excludeFromHistory: true, ct);
        await OpenAsync(ct);
        var emptied = false;
        Exception? failure = null;
        Offer? offer = null;
        try
        {
            if (!EmptyOwn()) throw new IOException("EmptyClipboard failed");
            emptied = true;
            offer = new Offer(System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            // Published before the offer: a clipboard monitor reacting to the change may ask for the text at once.
            lock (_offerGate) _offer = offer;
            User32.SetClipboardDataWithLastError(CfUnicodeText, 0);
            // A delayed-render set returns no handle, so 0 says nothing: the last error and the format's presence do.
            if (Marshal.GetLastPInvokeError() != 0 || !User32.IsClipboardFormatAvailable(CfUnicodeText))
            {
                lock (_offerGate) _offer = null;
                // Logged, so the feature can't go quietly off again: the paste still works, the old way.
                log?.Write(LogEvent.DelayedRenderUnavailable);
                SetBytes(CfUnicodeText, offer.Bytes);
                Array.Clear(offer.Bytes);
                offer = null;
            }
            SetMarkers(ClipboardPolicy.ExclusionFormats(excludeFromHistory: true));
        }
        catch (Exception ex) when (emptied)
        {
            failure = ex;
        }
        finally { User32.CloseClipboard(); }
        var sequence = User32.GetClipboardSequenceNumber();
        if (offer is not null) offer.Sequence = sequence;
        if (failure is not null)
        {
            lock (_offerGate)
            {
                if (_offer == offer) _offer = null;
            }
            throw new ClipboardChangedException(sequence, failure);
        }
        return sequence;
    }

    public async Task<PasteRead> WaitUntilReadAsync(uint ourSequence, TimeSpan max, CancellationToken ct)
    {
        Offer? offer;
        lock (_offerGate) offer = _offer;
        if (offer is null || offer.Sequence != ourSequence) return new PasteRead(PasteReadOutcome.NotOffered, ourSequence);
        try
        {
            return await offer.Read.Task.WaitAsync(max, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new PasteRead(PasteReadOutcome.TimedOut, ourSequence);
        }
    }

    /// <summary>WM_RENDERFORMAT on the owner thread: an app is reading the paste now. No open or close in here.</summary>
    public void OnRenderFormat(nint hwnd, uint format)
    {
        Offer? offer;
        lock (_offerGate) offer = _offer;
        if (offer is null || offer.Rendered || format != CfUnicodeText) return;
        var reader = Reader();
        SetBytes(CfUnicodeText, offer.Bytes);
        // The sequence first: StillOurs reads Rendered, then the sequence.
        offer.RenderedSequence = User32.GetClipboardSequenceNumber();
        offer.Rendered = true;
        Array.Clear(offer.Bytes);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(offer.OfferedAt);
        offer.Read.TrySetResult(new PasteRead(PasteReadOutcome.Read, User32.GetClipboardSequenceNumber(), reader, elapsed));
    }

    /// <summary>
    /// The reader is the process holding the clipboard open during the render. The paste went to the foreground window,
    /// so a reader in its process is the target; any other (a clipboard monitor, a sync tool) is not.
    /// </summary>
    private static PasteReader Reader()
    {
        var open = User32.GetOpenClipboardWindow();
        if (open == 0) return PasteReader.Unknown; // OpenClipboard(NULL): can't say, so treated as the target
        User32.GetWindowThreadProcessId(open, out var reader);
        User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out var target);
        return reader == target ? PasteReader.Target : PasteReader.Other;
    }

    /// <summary>WM_RENDERALLFORMATS: the owner window is going away while it still owns the clipboard (yolime exiting).</summary>
    public void OnRenderAllFormats(nint hwnd)
    {
        Offer? offer;
        lock (_offerGate) offer = _offer;
        if (offer is null || offer.Rendered) return;
        // A reader that's waiting on us must still be answered, so the retry pumps sent messages instead of sleeping.
        var deadline = Environment.TickCount64 + 500;
        while (!User32.OpenClipboard(hwnd))
        {
            if (Environment.TickCount64 > deadline) return;
            User32.PeekMessageW(out _, 0, 0, 0, PmQsSendMessage);
            Thread.Yield();
        }
        try
        {
            // Written for real, so a pending paste isn't lost; no empty, so nothing else on the clipboard changes.
            // Re-checked after the open: the pump above may have run a late restore's empty (WM_DESTROYCLIPBOARD), which
            // ends the offer and wipes its bytes. Writing then would put an empty text over the user's restored copy.
            bool current;
            lock (_offerGate) current = _offer == offer;
            if (current && User32.GetClipboardOwner() == hwnd && !offer.Rendered)
            {
                SetBytes(CfUnicodeText, offer.Bytes);
                offer.Rendered = true;
            }
        }
        finally { User32.CloseClipboard(); }
        Array.Clear(offer.Bytes);
        offer.Read.TrySetResult(new PasteRead(PasteReadOutcome.TimedOut, User32.GetClipboardSequenceNumber()));
    }

    /// <summary>WM_DESTROYCLIPBOARD: the clipboard was emptied, by our own next set or restore, or by a new copy.</summary>
    public void OnDestroyClipboard()
    {
        Offer? offer;
        lock (_offerGate)
        {
            offer = _offer;
            _offer = null;
        }
        if (offer is null) return;
        Array.Clear(offer.Bytes);
        // Only a foreign empty means "not ours any more"; our own (a restore, the next paste) just ends the offer.
        offer.Read.TrySetResult(_ownEmpty ? new PasteRead(PasteReadOutcome.TimedOut, offer.Sequence) : new PasteRead(PasteReadOutcome.Emptied, 0));
    }

    /// <summary>Our content, or our offer after a render moved the sequence number (the restore may not have seen it).</summary>
    private bool StillOurs(uint ours, uint current)
    {
        if (ClipboardPolicy.ShouldRestore(ours, current)) return true;
        lock (_offerGate) return _offer is { Rendered: true } o && o.Sequence == ours && o.RenderedSequence == current;
    }

    private bool EmptyOwn()
    {
        _ownEmpty = true;
        try
        {
            return User32.EmptyClipboard();
        }
        finally
        {
            _ownEmpty = false;
        }
    }

    public async Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct)
    {
        await OpenAsync(ct);
        try
        {
            // Checked while the clipboard is held: a copy made between an earlier check and the open would be lost.
            if (!StillOurs(ourSequence, User32.GetClipboardSequenceNumber())) return 0;
            EmptyOwn();
            foreach (var (format, data) in saved.Items) SetBytes(format, data);
            // Only the Can* markers: the Exclude/Viewer-Ignore ones would make the user's own content look like a
            // password manager's.
            SetMarkers(["CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"]);
        }
        finally { User32.CloseClipboard(); }
        // Read after closing, like SetTextAsync, so it names the finished state.
        return User32.GetClipboardSequenceNumber();
    }

    public async Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct)
    {
        await OpenAsync(ct);
        try
        {
            if (!StillOurs(ourSequence, User32.GetClipboardSequenceNumber())) return 0;
            EmptyOwn();
        }
        finally { User32.CloseClipboard(); }
        return User32.GetClipboardSequenceNumber();
    }

    // The retry loop lives in Core, so it (and what it records) is unit-tested.
    private Task OpenAsync(CancellationToken ct) => ClipboardOpen.RetryAsync(() => User32.OpenClipboard(owner), TimeProvider.System, ct);

    private static void SetMarkers(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var id = User32.RegisterClipboardFormatW(name);
            // The Can* formats need a DWORD 0; the others only need to exist.
            SetBytes(id, name.StartsWith("Can", StringComparison.Ordinal) ? BitConverter.GetBytes(0) : [0]);
        }
    }

    private static void SetBytes(uint format, byte[] data)
    {
        var handle = User32.GlobalAlloc(GmemMoveable, (nuint)Math.Max(1, data.Length));
        if (handle == 0) throw new OutOfMemoryException();
        var ptr = User32.GlobalLock(handle);
        if (ptr == 0)
        {
            User32.GlobalFree(handle);
            throw new OutOfMemoryException();
        }
        Marshal.Copy(data, 0, ptr, data.Length);
        User32.GlobalUnlock(handle);
        // On success the clipboard owns the memory; on failure it's still ours to free.
        if (User32.SetClipboardData(format, handle) == 0)
        {
            User32.GlobalFree(handle);
            if (format == CfUnicodeText) throw new IOException("couldn't set clipboard text");
        }
    }

    private static string? NameOf(uint format)
    {
        if (format < 0xC000) return null;
        var buf = new char[256];
        var n = User32.GetClipboardFormatNameW(format, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : null;
    }
}
