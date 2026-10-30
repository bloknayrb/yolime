using Yolime.Core.Clipboard;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Logging;

namespace Yolime.Core.Output;

/// <summary>
/// Delivers a result into the target app. It waits for the user's fingers to leave the modifiers, looks at the
/// target again, plans with <see cref="OutputPlanner"/>, then pastes by borrowing the clipboard and giving it back.
/// </summary>
/// <remarks>
/// Order matters for privacy: the target is checked after the modifier wait (the user may Alt+Tab while holding
/// keys), and the foreground window is checked again right before the paste, so Ctrl+V can only land where the
/// plan looked. Only one clipboard restore is ever pending; a new delivery restores it first, so back-to-back
/// dictations can't save the previous dictation as the user's "original".
/// </remarks>
public sealed class OutputExecutor(
    IClipboard clipboard,
    IInjector injector,
    ITargetReader targets,
    INoticeSink notices,
    Func<Guid, TargetSnapshot?> startSnapshot,
    TimeProvider time,
    ILog log,
    ClipboardFreshness? freshness = null,
    IPreviewPrompt? preview = null,
    IEditableFocusWaiter? focusWaiter = null) : IOutputSink
{
    /// <summary>How long a Reply from a read-only selection waits for the user to click where it should go.</summary>
    public static readonly TimeSpan PendingPasteLimit = TimeSpan.FromSeconds(10);

    /// <summary>Raised when a Reply starts waiting for a click (seconds to wait), and with 0 when the wait ends.</summary>
    public event Action<int>? PendingPaste;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Waits on the user (the preview card) happen outside _gate and end here on exit, so they never hold up the flush.
    private CancellationTokenSource _waits = new();
    private volatile bool _closed;
    private CancellationTokenSource? _restoreDelay;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, OutputBreakdown> _breakdowns = new();

    /// <summary>The paste's timing breakdown for a run, taken once (the pipeline puts it in the run's timing line).</summary>
    public OutputBreakdown? TakeBreakdown(Guid runId) => _breakdowns.TryRemove(runId, out var b) ? b : null;

    /// <summary>Times the steps of one paste. Not thread-safe: one delivery owns it.</summary>
    private sealed class Laps(TimeProvider time)
    {
        private long _mark = time.GetTimestamp();
        public long GateWait, ModifierWait, Snapshot, Flush, Save, Set, Paste;

        // Begun with the laps, in DeliverAsync's own flow, so it counts this paste's opens: the save and set, and on
        // the rarer paths the fallback re-set and an error restore, which no lap covers. Each background restore
        // begins its own meter, so neither the previous paste's (flushed here) nor this one's counts.
        public ClipboardOpenMeter Opens { get; } = ClipboardOpenMeter.Begin();

        public long Lap()
        {
            var now = time.GetTimestamp();
            var ms = (long)time.GetElapsedTime(_mark, now).TotalMilliseconds;
            _mark = now;
            return ms;
        }

        public OutputBreakdown ToRecord()
        {
            var (openMs, openRetries) = Opens.Close();
            return new(GateWait, ModifierWait, Snapshot, Flush, Save, Set, Paste, openMs, openRetries);
        }
    }

    /// <summary>The background restore of the last paste, if any. Tests await it.</summary>
    public Task PendingRestore { get; private set; } = Task.CompletedTask;

    public async Task<DeliveryKind> DeliverAsync(OutputItem item, CancellationToken ct)
    {
        if (item.PreviewRequested && preview is not null)
        {
            using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct, _waits.Token);
            try
            {
                await injector.WaitForModifiersUpAsync(waits.Token);
            }
            catch (TimeoutException)
            {
                // Still held: the card shows anyway, and Insert checks again before it pastes.
            }
            // Planned first so a password field refuses without ever showing the text.
            var first = OutputPlanner.Plan(item, startSnapshot(item.RunId), targets.Snapshot());
            if (first.Action == OutputAction.Refuse)
            {
                Notify(first.Notice);
                return DeliveryKind.Refused;
            }
            switch (await preview.AskAsync(first.Text, waits.Token))
            {
                case PreviewChoice.Discard:
                    return DeliveryKind.Discarded;
                case PreviewChoice.Expired:
                    notices.Show(new Notice(NoticeKind.PreviewKept));
                    return DeliveryKind.Discarded;
                case PreviewChoice.Copy:
                    await _gate.WaitAsync(ct);
                    try
                    {
                        await FlushCoreAsync();
                        return await CopyAsync(first.Text, OutputNotice.CopiedClipboardOnly, ct);
                    }
                    finally
                    {
                        _gate.Release();
                    }
            }
            // Insert: the normal path below plans again against the target as it is now, after the card.
        }

        if (item.Mode == Mode.Reply && focusWaiter is not null && await WaitForEditableAsync(item, ct) is { } chosen)
            return chosen;

        var laps = new Laps(time);
        await _gate.WaitAsync(ct);
        laps.GateWait = laps.Lap();
        try
        {
            // After the exit flush nothing pastes: a card or click that ended just before it mustn't land later.
            if (_closed) throw new OperationCanceledException("closing");
            var modifiersStuck = false;
            try
            {
                await injector.WaitForModifiersUpAsync(ct);
            }
            catch (TimeoutException)
            {
                modifiersStuck = true;
            }
            laps.ModifierWait = laps.Lap();

            var now = targets.Snapshot();
            laps.Snapshot = laps.Lap();
            var decision = OutputPlanner.Plan(item, startSnapshot(item.RunId), now);
            if (decision.Action == OutputAction.Refuse)
            {
                Notify(decision.Notice);
                return DeliveryKind.Refused;
            }

            await FlushCoreAsync();
            laps.Flush = laps.Lap();
            if (decision.Action == OutputAction.ClipboardOnly || modifiersStuck)
                return await CopyAsync(decision.Text, item.QuietCopy ? OutputNotice.None : decision.Action == OutputAction.ClipboardOnly ? decision.Notice : OutputNotice.CopiedTargetChanged, ct, laps);

            return await SwapAndPasteAsync(decision, now.Hwnd, item.QuietCopy, TerminalDetector.IsRemote(now), ct, laps);
        }
        finally
        {
            // Bounded: deliveries nobody takes a breakdown for (paste-last, failures) mustn't grow it all session.
            if (_breakdowns.Count > 32) _breakdowns.Clear();
            _breakdowns[item.RunId] = laps.ToRecord();
            _gate.Release();
        }
    }

    /// <summary>
    /// The pending paste: a Reply whose target is positively read-only (the selection was in a reading pane) waits,
    /// outside the lock, for the user to click a field in the same app, then goes through the normal plan with that
    /// field as the start target, so every safety rule (password, admin, terminal) runs against it. Null means
    /// "carry on normally" (not read-only, or no click in time: the normal path then copies).
    /// </summary>
    private async Task<DeliveryKind?> WaitForEditableAsync(OutputItem item, CancellationToken ct)
    {
        var atStart = startSnapshot(item.RunId);
        if (atStart is null) return null;
        var first = OutputPlanner.Plan(item, atStart, targets.Snapshot());
        if (first.Notice != OutputNotice.CopiedNotEditable) return null;

        using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct, _waits.Token);
        PendingPaste?.Invoke((int)PendingPasteLimit.TotalSeconds);
        TargetSnapshot? clicked;
        try
        {
            clicked = await focusWaiter!.WaitAsync(atStart.ProcessId, PendingPasteLimit, waits.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !_closed)
        {
            // A hold-key tap while waiting means "never mind where": the reply is copied, as on a timeout.
            return await CopyNowAsync(first.Text, item.QuietCopy ? OutputNotice.None : OutputNotice.CopiedNotEditable);
        }
        finally
        {
            PendingPaste?.Invoke(0);
        }
        if (clicked is null) return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (_closed) throw new OperationCanceledException("closing");
            var modifiersStuck = false;
            try
            {
                await injector.WaitForModifiersUpAsync(ct);
            }
            catch (TimeoutException)
            {
                // Ctrl still held after a Ctrl-click: pasting would turn into another shortcut, so copy instead.
                modifiersStuck = true;
            }
            var now = targets.Snapshot();
            var decision = OutputPlanner.Plan(item, clicked, now);
            if (decision.Action == OutputAction.Refuse)
            {
                Notify(decision.Notice);
                return DeliveryKind.Refused;
            }
            await FlushCoreAsync();
            if (decision.Action == OutputAction.ClipboardOnly || modifiersStuck)
                return await CopyAsync(decision.Text, item.QuietCopy ? OutputNotice.None : modifiersStuck ? OutputNotice.CopiedTargetChanged : decision.Notice, ct);
            return await SwapAndPasteAsync(decision, now.Hwnd, item.QuietCopy, TerminalDetector.IsRemote(now), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DeliveryKind> CopyNowAsync(string text, OutputNotice notice)
    {
        await _gate.WaitAsync();
        try
        {
            await FlushCoreAsync();
            return await CopyAsync(text, notice, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A copy the user asked for ("Use raw"): kept in clipboard history, with its own notice.</summary>
    public async Task CopyForUserAsync(string text, NoticeKind notice, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await FlushCoreAsync();
            Own(await clipboard.SetTextAsync(text, excludeFromHistory: false, ct));
            notices.Show(new Notice(notice));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Borrows the clipboard for a paste. From the moment it is touched, every way out gives it back, except a paste
    /// (the background restore takes over) and a deliberate copy for the user. The saved bytes are always wiped.
    /// </summary>
    private async Task<DeliveryKind> SwapAndPasteAsync(OutputDecision decision, nint target, bool quiet, bool remote, CancellationToken ct, Laps? laps = null)
    {
        var saved = await clipboard.SaveAsync(ct);
        if (laps is not null) laps.Save = laps.Lap();
        uint ours = 0; // 0 until the clipboard has been touched
        var restoreOnExit = true;
        try
        {
            try
            {
                // Offered by delayed rendering, so the restore can wait for the read. A remote window's read comes from
                // the redirector (rdpclip), not the app, so it keeps its own longer rule and gets the text outright.
                ours = Own(await (remote
                    ? clipboard.SetTextAsync(decision.Text, excludeFromHistory: true, ct)
                    : clipboard.OfferPasteAsync(decision.Text, ct)));
            }
            catch (ClipboardChangedException ex)
            {
                ours = Own(ex.Sequence); // emptied or half-written: still ours to put back
                throw;
            }
            if (laps is not null) laps.Set = laps.Lap();
            ct.ThrowIfCancellationRequested();

            var pasted = targets.Foreground() == target && injector.Paste(decision.Chord);
            if (laps is not null) laps.Paste = laps.Lap();
            if (pasted)
            {
                restoreOnExit = false;
                var delay = new CancellationTokenSource();
                _restoreDelay = delay;
                PendingRestore = RestoreLaterAsync(saved, ours, target, remote ? ClipboardPolicy.RemoteRestoreDelay : ClipboardPolicy.RestoreDelay,
                    offered: !remote, delay.Token);
                saved = null; // the background restore owns it now
                return DeliveryKind.Pasted;
            }

            // The window changed in the last instant, or the input was blocked: hand the text over instead. Our text
            // is already on the clipboard, so a late cancel or a failed re-set still leaves it pasteable.
            try
            {
                Own(await clipboard.SetTextAsync(decision.Text, excludeFromHistory: false, CancellationToken.None));
            }
            catch (ClipboardChangedException ex)
            {
                // The re-set emptied the clipboard and failed: neither our text nor theirs is there. Put theirs back.
                ours = Own(ex.Sequence);
                throw;
            }
            catch (Exception ex)
            {
                // Failed before touching the clipboard: our first copy is still there to paste.
                log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Output), LogField.Error(ex));
            }
            restoreOnExit = false;
            if (!quiet) Notify(OutputNotice.CopiedTargetChanged);
            return DeliveryKind.Copied;
        }
        catch when (restoreOnExit && ours != 0)
        {
            try
            {
                await RestoreNowAsync(saved!, ours);
            }
            catch (Exception ex)
            {
                log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Output), LogField.Error(ex));
            }
            throw;
        }
        finally
        {
            saved?.Wipe();
        }
    }

    /// <summary>Finishes any pending restore now (on exit). Waits for a delivery in flight, so its restore is included.</summary>
    public async Task FlushAsync()
    {
        // End any card or wait first: it holds nothing, but its delivery mustn't paste after the flush.
        _closed = true;
        var waits = Interlocked.Exchange(ref _waits, new CancellationTokenSource());
        waits.Cancel();
        // ConfigureAwait(false): the exit path blocks the UI thread on this, so no continuation may need it.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await FlushCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushCoreAsync()
    {
        _restoreDelay?.Cancel();
        await PendingRestore.ConfigureAwait(false);
    }

    private async Task<DeliveryKind> CopyAsync(string text, OutputNotice notice, CancellationToken ct, Laps? laps = null)
    {
        // "Copied, not pasted" stays in clipboard history: the user may need it after the next copy.
        Own(await clipboard.SetTextAsync(text, excludeFromHistory: false, ct));
        if (laps is not null) laps.Set = laps.Lap();
        // None here means "the run explains itself" (QuietCopy): the copy shows nothing of its own.
        Notify(notice);
        return DeliveryKind.Copied;
    }

    /// <summary>Tells freshness tracking that this clipboard state is ours, never a user copy for Reply context.</summary>
    private uint Own(uint sequence)
    {
        if (sequence != 0) freshness?.MarkOwn(sequence);
        return sequence;
    }

    private async Task RestoreLaterAsync(SavedClipboard saved, uint ours, nint target, TimeSpan restoreDelay, bool offered, CancellationToken skipDelay)
    {
        var start = time.GetTimestamp();
        long idleMs = 0;
        // Its own meter: contention is likeliest right after the paste, while the target is still reading.
        var opens = ClipboardOpenMeter.Begin();
        var read = new PasteRead(PasteReadOutcome.NotOffered, ours);
        try
        {
            try
            {
                // Let the target read the clipboard before we take it back: a busy app can be slow to paste.
                await targets.WaitForIdleAsync(target, skipDelay);
                idleMs = (long)time.GetElapsedTime(start).TotalMilliseconds;
                await Task.Delay(restoreDelay, time, skipDelay);
                if (offered)
                {
                    // A slow app (Outlook, Electron) may read after that: wait for its read, so it never gets the old content.
                    var waitStart = time.GetTimestamp();
                    read = await clipboard.WaitUntilReadAsync(ours, ClipboardPolicy.MaxReadWait, skipDelay);
                    if (read.Outcome == PasteReadOutcome.Emptied)
                    {
                        // Someone else's copy replaced ours: theirs stays, and there is nothing of ours to take back.
                        log.Write(LogEvent.ClipboardRestoreSkipped, LogField.Tag("read", read.Outcome), LogField.Ms("total", (long)time.GetElapsedTime(start).TotalMilliseconds));
                        return;
                    }
                    // Rendering can move the sequence number: the new one is still our paste, never a fresh user copy.
                    if (read.Sequence != 0 && read.Sequence != ours) ours = Own(read.Sequence);
                    if (read.Outcome == PasteReadOutcome.Read) await AfterReadAsync(read.Reader, waitStart, target, skipDelay);
                }
            }
            catch (OperationCanceledException)
            {
                // Flushed by the next delivery: restore right away.
            }
            catch (Exception ex)
            {
                // The wait failed (the target went away): still give the clipboard back.
                log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Output), LogField.Error(ex));
            }

            var restoreStart = time.GetTimestamp();
            await RestoreNowAsync(saved, ours);
            // How long the target was given, and how long the restore itself took (PR7).
            var fields = new List<LogField>
            {
                LogField.Ms("idleWait", idleMs), LogField.Ms("total", (long)time.GetElapsedTime(start).TotalMilliseconds),
                LogField.Ms("restore", (long)time.GetElapsedTime(restoreStart).TotalMilliseconds), LogField.Tag("read", read.Outcome),
            };
            // Who read it and when (content-free): the evidence for whether the wait earns its keep.
            if (read.Outcome == PasteReadOutcome.Read) fields.Add(LogField.Tag("reader", read.Reader));
            if (read.ReadAfterOffer is { } readAt) fields.Add(LogField.Ms("readAfterOffer", (long)readAt.TotalMilliseconds));
            var (openMs, openRetries) = opens.Close();
            fields.Add(LogField.Ms("openMs", openMs));
            fields.Add(LogField.Count("openRetries", openRetries));
            log.Write(LogEvent.ClipboardRestored, [.. fields]);
        }
        catch (Exception ex)
        {
            log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Output), LogField.Error(ex));
        }
        finally
        {
            saved.Wipe();
        }
    }

    /// <summary>After the offered paste was read: how long to leave it before the restore.</summary>
    private async Task AfterReadAsync(PasteReader reader, long waitStart, nint target, CancellationToken skipDelay)
    {
        if (reader == PasteReader.Other)
        {
            // A clipboard monitor rendered it, so the target's own later read can't be seen: it gets the whole wait.
            var left = ClipboardPolicy.MaxReadWait - time.GetElapsedTime(waitStart);
            if (left > TimeSpan.Zero) await Task.Delay(left, time, skipDelay);
            return;
        }
        // OLE readers (Word, Outlook) open the clipboard once per format: a restore between them mixes the paste.
        await targets.WaitForIdleAsync(target, skipDelay);
        await Task.Delay(ClipboardPolicy.ReadGrace, time, skipDelay);
    }

    /// <summary>Gives the clipboard back, then wipes the saved bytes whatever happens.</summary>
    private async Task RestoreNowAsync(SavedClipboard saved, uint ours)
    {
        try
        {
            // A password manager's copy is cleared, never written back: a restore would re-publish it without its
            // "don't record me" markers and reset the manager's own clear timer.
            if (saved.IsSensitive)
                Own(await clipboard.ClearIfOursAsync(ours, CancellationToken.None));
            else if (saved.Items.Count > 0 || !saved.HadContent)
            {
                // The user's copy is back: it keeps the age it had, so a reply can still use a fresh copy.
                if (await clipboard.RestoreAsync(saved, ours, CancellationToken.None) is var restored and not 0)
                    freshness?.MarkRestored(restored);
            }
            // Otherwise the original couldn't be saved: leave our text rather than empty the clipboard.
        }
        finally
        {
            saved.Wipe();
        }
    }

    private void Notify(OutputNotice n)
    {
        // Why a result wasn't pasted, as the enum only: the first thing to know when "it copied instead".
        if (n != OutputNotice.None) log.Write(LogEvent.OutputNotPasted, LogField.Tag("reason", n));
        if (Notices.For(n) is { } notice) notices.Show(notice);
    }
}
