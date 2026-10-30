using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Clipboard;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Output;

namespace Yolime.Core.Tests.Output;

public class OutputExecutorTests
{
    private static readonly TargetSnapshot Word = new(100, 1, "WINWORD", "OpusApp", "42.1", "_WwG", false, true, false);

    private sealed class FakeClipboard : IClipboard
    {
        public uint Sequence = 1;
        public string? Text = "user original";
        public bool InHistory = true;
        public bool Sensitive;
        public List<string> Log { get; } = [];

        /// <summary>Set: every open finds the clipboard busy once, through the real retry loop.</summary>
        public bool BusyOnce;

        private async Task OpenBusyOnce()
        {
            var first = true;
            await ClipboardOpen.RetryAsync(() => !(first && !(first = false)), TimeProvider.System, CancellationToken.None);
        }

        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => BusyOnce ? SaveBusy(ct) : SaveNow();

        private async Task<SavedClipboard> SaveBusy(CancellationToken ct)
        {
            await OpenBusyOnce();
            return await SaveNow();
        }

        private Task<SavedClipboard> SaveNow()
        {
            Log.Add("save");
            var items = Text is null ? new List<(uint, byte[])>() : [(13u, System.Text.Encoding.UTF8.GetBytes(Text))];
            return Task.FromResult(new SavedClipboard(items, Sensitive, Text is not null));
        }

        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct) =>
            BusyOnce ? SetBusy(text, excludeFromHistory) : SetNow(text, excludeFromHistory);

        private async Task<uint> SetBusy(string text, bool excludeFromHistory)
        {
            await OpenBusyOnce();
            return await SetNow(text, excludeFromHistory);
        }

        /// <summary>Set (with <see cref="Time"/>): each set takes this long on the test's clock.</summary>
        public TimeSpan SetTakes;

        private Task<uint> SetNow(string text, bool excludeFromHistory)
        {
            if (SetTakes > TimeSpan.Zero) (Time as FakeTimeProvider)?.Advance(SetTakes);
            Log.Add(excludeFromHistory ? "set-transient" : "set-kept");
            Text = text;
            InHistory = !excludeFromHistory;
            return Task.FromResult(++Sequence);
        }

        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) =>
            BusyOnce ? RestoreBusy(saved, ourSequence) : RestoreNow(saved, ourSequence);

        private async Task<uint> RestoreBusy(SavedClipboard saved, uint ourSequence)
        {
            await OpenBusyOnce();
            return await RestoreNow(saved, ourSequence);
        }

        private Task<uint> RestoreNow(SavedClipboard saved, uint ourSequence)
        {
            if (ourSequence != Sequence) return Task.FromResult(0u);
            Log.Add("restore");
            Text = saved.Items.Count == 0 ? null : System.Text.Encoding.UTF8.GetString(saved.Items[0].Data);
            return Task.FromResult(++Sequence);
        }

        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct)
        {
            if (ourSequence != Sequence) return Task.FromResult(0u);
            Log.Add("clear");
            Text = null;
            return Task.FromResult(++Sequence);
        }

        public void UserCopies(string text)
        {
            Text = text;
            Sequence++;
        }

        /// <summary>How many pastes were offered by delayed rendering rather than written outright.</summary>
        public int Offers;

        public Task<uint> OfferPasteAsync(string text, CancellationToken ct)
        {
            Offers++;
            return SetTextAsync(text, excludeFromHistory: true, ct);
        }

        /// <summary>Set: the guard tracks the offer's read. The test completes it when the paste "is read".</summary>
        public TaskCompletionSource<PasteRead>? Read;
        public TimeProvider? Time;
        public int ReadWaits;

        public async Task<PasteRead> WaitUntilReadAsync(uint ourSequence, TimeSpan max, CancellationToken ct)
        {
            ReadWaits++;
            if (Read is not { } r) return new PasteRead(PasteReadOutcome.NotOffered, ourSequence);
            try
            {
                return await r.Task.WaitAsync(max, Time!, ct);
            }
            catch (TimeoutException)
            {
                return new PasteRead(PasteReadOutcome.TimedOut, ourSequence);
            }
        }
    }

    private sealed class FakeInjector : IInjector
    {
        public bool ModifiersStuck;
        public bool Blocked;
        public List<PasteChord> Pastes { get; } = [];
        public Task WaitForModifiersUpAsync(CancellationToken ct) => ModifiersStuck ? throw new TimeoutException() : Task.CompletedTask;
        public bool Paste(PasteChord chord)
        {
            if (Blocked) return false;
            Pastes.Add(chord);
            return true;
        }
    }

    private sealed class FakeTargets : ITargetReader
    {
        public TargetSnapshot Now = Word;
        public nint ForegroundHwnd = Word.Hwnd;
        public TargetSnapshot Snapshot() => Now;
        public nint Foreground() => ForegroundHwnd;
        public Task WaitForIdleAsync(nint hwnd, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Notes : INoticeSink
    {
        public List<Notice> Shown { get; } = [];
        public void Show(Notice notice) => Shown.Add(notice);
    }

    private readonly FakeClipboard _clip = new();
    private readonly FakeInjector _inj = new();
    private readonly FakeTargets _targets = new();
    private readonly Notes _notes = new();
    private readonly FakeTimeProvider _time = new();
    private TargetSnapshot? _start = Word;

    private readonly RecordingLog _log = new();

    private OutputExecutor Executor() => new(_clip, _inj, _targets, _notes, _ => _start, _time, _log);

    private static OutputItem Item(string text = "dictated", DeliveryIntent intent = DeliveryIntent.Paste) =>
        new(text, intent, false, Mode.Dictate, Guid.NewGuid());

    private async Task Settle(OutputExecutor e)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        await e.PendingRestore;
    }

    // Delayed rendering: the paste is offered, and the restore waits for the target's read (the delayed-render plan).

    private TaskCompletionSource<PasteRead> Unread()
    {
        _clip.Time = _time;
        _clip.Read = new TaskCompletionSource<PasteRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _clip.Read;
    }

    private async Task Advance(TimeSpan by)
    {
        _time.Advance(by);
        await Task.Delay(30);
    }

    [Fact]
    public async Task APaste_IsOfferedForDelayedRendering()
    {
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        Assert.Equal(1, _clip.Offers);
        await Settle(e);
    }

    [Fact]
    public async Task ARemoteTarget_IsWrittenOutright()
    {
        // The redirector (rdpclip) reads for an RDP or VM window, never the app itself: today's rule stands.
        _targets.Now = Word with { ProcessName = "mstsc", WindowClass = "TscShellContainerClass" };
        _start = _targets.Now;
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        Assert.Equal(0, _clip.Offers);
        _time.Advance(ClipboardPolicy.RemoteRestoreDelay);
        await e.PendingRestore;
        Assert.Equal(0, _clip.ReadWaits);
    }

    [Fact]
    public async Task ACopyForTheUser_IsNeverOffered()
    {
        var e = Executor();
        await e.DeliverAsync(Item(intent: DeliveryIntent.ClipboardOnly), default);
        Assert.Equal(0, _clip.Offers);
    }

    [Fact]
    public async Task ASlowTarget_GetsItsPasteBeforeTheClipboardIsPutBack()
    {
        // Outlook or an Electron app reading after the usual 800 ms would otherwise paste the user's old clipboard.
        var read = Unread();
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("dictated", _clip.Text);
        read.SetResult(new PasteRead(PasteReadOutcome.Read, _clip.Sequence, PasteReader.Target, TimeSpan.FromSeconds(2)));
        await Task.Delay(30); // the continuation registers its next timer before the clock moves
        await Advance(TimeSpan.FromMilliseconds(300));
        await e.PendingRestore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task AReadBeforeTheMinimum_StillWaitsTheMinimum()
    {
        // A clipboard manager reading at once must not make the restore earlier than today.
        var read = Unread();
        read.SetResult(new PasteRead(PasteReadOutcome.Read, _clip.Sequence + 1, PasteReader.Target, TimeSpan.FromMilliseconds(5)));
        await Task.Delay(30); // the continuation registers its next timer before the clock moves
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromMilliseconds(700));
        Assert.Equal("dictated", _clip.Text);
        await Advance(TimeSpan.FromMilliseconds(400));
        await Advance(TimeSpan.FromMilliseconds(300));
        await e.PendingRestore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("user original", _clip.Text);
        Assert.Equal(1, _clip.ReadWaits);
    }

    [Fact]
    public async Task AfterARead_TheRestoreWaitsAGraceForTheOtherFormats()
    {
        // OLE readers (Word, Outlook) open the clipboard once per format: a restore between them mixes the paste.
        var read = Unread();
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromSeconds(1));
        read.SetResult(new PasteRead(PasteReadOutcome.Read, _clip.Sequence, PasteReader.Target, TimeSpan.FromSeconds(1)));
        await Task.Delay(30); // the continuation registers its next timer before the clock moves
        await Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal("dictated", _clip.Text);
        await Advance(TimeSpan.FromMilliseconds(200));
        await e.PendingRestore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task ANeverReadPaste_IsRestoredAfterTheMaximumWait()
    {
        Unread();
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromMilliseconds(800));
        await Advance(TimeSpan.FromMilliseconds(2900));
        Assert.Equal("dictated", _clip.Text);
        await Advance(TimeSpan.FromMilliseconds(200));
        await e.PendingRestore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task AReadByAnotherProcess_StillWaitsTheFullTime()
    {
        // A clipboard monitor rendered it, so the target's own later read can't be seen: don't restore early.
        var read = Unread();
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromMilliseconds(800));
        read.SetResult(new PasteRead(PasteReadOutcome.Read, _clip.Sequence, PasteReader.Other, TimeSpan.FromMilliseconds(900)));
        await Task.Delay(30); // the continuation registers its next timer before the clock moves
        await Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("dictated", _clip.Text);
        await Advance(TimeSpan.FromSeconds(2.1));
        await e.PendingRestore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task ANewCopyDuringTheWait_IsLeftAlone()
    {
        // Someone else emptied the clipboard (the user copied something new): theirs stays.
        var read = Unread();
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromSeconds(1));
        _clip.UserCopies("new copy");
        read.SetResult(new PasteRead(PasteReadOutcome.Emptied, 0));
        await Task.Delay(30); // the continuation registers its next timer before the clock moves
        await e.PendingRestore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("new copy", _clip.Text);
        Assert.DoesNotContain("restore", _clip.Log);
    }

    [Fact]
    public async Task ASensitiveOriginal_IsClearedAfterTheReadToo()
    {
        // Ruling 11: the wait holds the dictation, never the password, so a slow reader still gets its paste.
        _clip.Sensitive = true;
        var read = Unread();
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("dictated", _clip.Text);
        read.SetResult(new PasteRead(PasteReadOutcome.Read, _clip.Sequence, PasteReader.Target, TimeSpan.FromSeconds(2)));
        await Task.Delay(30); // the continuation registers its next timer before the clock moves
        await Advance(TimeSpan.FromMilliseconds(300));
        await e.PendingRestore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_clip.Text);
    }

    [Fact]
    public async Task TheFlush_EndsTheReadWait()
    {
        // The exit flush has a 4 s budget, and the read wait alone could take 3.
        Unread();
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("dictated", _clip.Text); // still waiting for the read: the flush is what ends it
        await e.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task TheNextDelivery_DoesntWaitForAnUnreadPaste()
    {
        // Accepted (ruling 10): a quick second dictation restores the first at once, as today.
        Unread();
        var e = Executor();
        await e.DeliverAsync(Item("first"), default);
        await Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("first", _clip.Text); // still waiting for the read
        _clip.Read = null;
        Assert.Equal(DeliveryKind.Pasted, await e.DeliverAsync(Item("second"), default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("second", _clip.Text);
        await Settle(e);
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task ARenderedSequence_IsStillOurs()
    {
        // If rendering moves the sequence number, the new one is our paste, never a fresh user copy for Reply.
        var fresh = new ClipboardFreshness(_time);
        var read = Unread();
        var e = new OutputExecutor(_clip, _inj, _targets, _notes, _ => _start, _time, new RecordingLog(), fresh);
        await e.DeliverAsync(Item(), default);
        await Advance(TimeSpan.FromSeconds(1));
        var rendered = ++_clip.Sequence;
        fresh.OnChanged(rendered);
        read.SetResult(new PasteRead(PasteReadOutcome.Read, rendered, PasteReader.Target, TimeSpan.FromSeconds(1)));
        await Task.Delay(30); // the continuation registers its next timer before the clock moves
        await Advance(TimeSpan.FromMilliseconds(300));
        await e.PendingRestore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("user original", _clip.Text);
        Assert.Null(fresh.UserCopyAgeMs(rendered));
    }

    [Fact]
    public async Task APaste_RecordsWhereItsTimeWent()
    {
        var e = Executor();
        var item = Item();
        Assert.Equal(DeliveryKind.Pasted, await e.DeliverAsync(item, default));
        var b = e.TakeBreakdown(item.RunId);
        Assert.NotNull(b);
        Assert.True(b!.GateWaitMs >= 0 && b.FlushMs >= 0 && b.ModifierWaitMs >= 0 && b.SnapshotMs >= 0);
        Assert.True(b.ClipboardSaveMs >= 0 && b.ClipboardSetMs >= 0 && b.PasteMs >= 0);
        Assert.Null(e.TakeBreakdown(item.RunId)); // taken once, so the table can't grow
    }

    [Fact]
    public async Task APaste_RecordsItsClipboardOpenRetries_NotTheRestores()
    {
        // Every open finds the clipboard busy once. The second paste flushes the first one's restore (a busy open
        // too) before it saves: its breakdown must hold only its own save and set. (The restore runs in the first
        // paste's context, so this pins that the meter is per-flow, not global; the restore's own numbers are
        // pinned by TheRestore_LogsItsOwnOpenRetries.)
        _clip.BusyOnce = true;
        var e = Executor();
        await e.DeliverAsync(Item("first"), default);
        Assert.Equal("first", _clip.Text); // its restore is still pending
        var second = Item("second");
        await e.DeliverAsync(second, default);
        Assert.Contains("restore", _clip.Log);
        var b = e.TakeBreakdown(second.RunId)!;
        Assert.Equal(2, b.ClipboardOpenRetries);
        Assert.True(b.ClipboardOpenMs >= 15, $"{b.ClipboardOpenMs} ms");
        await Settle(e);
    }

    [Fact]
    public async Task TheRestore_LogsItsOwnOpenRetries()
    {
        // The restore runs right after the paste, while the target may still hold the clipboard: its own retries are
        // logged with it, not lost in the paste's (closed) meter.
        _clip.BusyOnce = true;
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Settle(e);
        var restored = _log.Lines.Single(l => l.Event == Yolime.Core.Logging.LogEvent.ClipboardRestored);
        Assert.Contains("openRetries=1", restored.Fields);
    }

    [Fact]
    public async Task ACopyOnly_LapsItsSet()
    {
        // Its open time must fall inside a lap, or the breakdown can't account for it.
        _inj.ModifiersStuck = true;
        _clip.BusyOnce = true;
        _clip.Time = _time;
        _clip.SetTakes = TimeSpan.FromMilliseconds(7);
        var e = Executor();
        var item = Item();
        Assert.Equal(DeliveryKind.Copied, await e.DeliverAsync(item, default));
        var b = e.TakeBreakdown(item.RunId)!;
        Assert.Equal(1, b.ClipboardOpenRetries);
        Assert.Equal(7, b.ClipboardSetMs);
    }

    [Fact]
    public async Task Paste_SetsTransientCopy_Pastes_ThenRestoresOriginal()
    {
        var e = Executor();
        Assert.Equal(DeliveryKind.Pasted, await e.DeliverAsync(Item(), default));
        Assert.Equal([PasteChord.CtrlV], _inj.Pastes);
        Assert.False(_clip.InHistory);
        Assert.Equal("dictated", _clip.Text);

        await Settle(e);
        Assert.Equal("user original", _clip.Text);
        Assert.Equal(["save", "set-transient", "restore"], _clip.Log);
        Assert.Empty(_notes.Shown);
    }

    [Fact]
    public async Task Freshness_PasteCopyIsOurs_RestoreInheritsTheUsersCopy()
    {
        var fresh = new Yolime.Core.Clipboard.ClipboardFreshness(_time);
        fresh.OnChanged(_clip.Sequence); // the user's own copy
        var e = new OutputExecutor(_clip, _inj, _targets, _notes, _ => _start, _time, new RecordingLog(), fresh);

        await e.DeliverAsync(Item(), default);
        Assert.Null(fresh.UserCopyAgeMs(_clip.Sequence)); // our transient paste copy
        await Settle(e);
        Assert.Equal("user original", _clip.Text);
        Assert.NotNull(fresh.UserCopyAgeMs(_clip.Sequence)); // the restore carries the user's copy's age
    }

    [Fact]
    public async Task Freshness_CopiedResultIsOurs()
    {
        var fresh = new Yolime.Core.Clipboard.ClipboardFreshness(_time);
        fresh.OnChanged(_clip.Sequence);
        _targets.ForegroundHwnd = 999;
        var e = new OutputExecutor(_clip, _inj, _targets, _notes, _ => _start, _time, new RecordingLog(), fresh);
        Assert.Equal(DeliveryKind.Copied, await e.DeliverAsync(Item(), default));
        Assert.Null(fresh.UserCopyAgeMs(_clip.Sequence));

        _start = null; // ClipboardOnly path
        await e.DeliverAsync(Item(), default);
        Assert.Null(fresh.UserCopyAgeMs(_clip.Sequence));
    }

    [Fact]
    public async Task RemoteDesktop_WaitsLongerBeforeRestoring()
    {
        // mstsc's clipboard redirection reads late: an early restore would paste the user's old clipboard remotely.
        var rdp = Word with { ProcessName = "mstsc", WindowClass = "TscShellContainerClass" };
        _start = rdp;
        _targets.Now = rdp;
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        _time.Advance(ClipboardPolicy.RestoreDelay + TimeSpan.FromMilliseconds(50));
        await Task.Delay(50);
        Assert.Equal("dictated", _clip.Text);
        _time.Advance(ClipboardPolicy.RemoteRestoreDelay);
        await e.PendingRestore;
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task ForegroundChangedJustBeforePaste_CopiesInstead()
    {
        _targets.ForegroundHwnd = 999;
        var e = Executor();
        Assert.Equal(DeliveryKind.Copied, await e.DeliverAsync(Item(), default));
        Assert.Empty(_inj.Pastes);
        Assert.Equal("dictated", _clip.Text);
        Assert.True(_clip.InHistory);
        Assert.Equal(NoticeKind.Copied, Assert.Single(_notes.Shown).Kind);
    }

    [Fact]
    public async Task PasteBlocked_CopiesInstead()
    {
        _inj.Blocked = true;
        Assert.Equal(DeliveryKind.Copied, await Executor().DeliverAsync(Item(), default));
        Assert.True(_clip.InHistory);
    }

    [Fact]
    public async Task ModifiersStuck_CopiesWithoutPasting()
    {
        _inj.ModifiersStuck = true;
        Assert.Equal(DeliveryKind.Copied, await Executor().DeliverAsync(Item(), default));
        Assert.Empty(_inj.Pastes);
        Assert.Equal("dictated", _clip.Text);
    }

    [Fact]
    public async Task SecondPasteBeforeRestore_FlushesFirst_OriginalSurvives()
    {
        var e = Executor();
        await e.DeliverAsync(Item("first"), default);
        await e.DeliverAsync(Item("second"), default);
        await Settle(e);
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task UserCopyDuringRestoreWindow_NotOverwritten()
    {
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        _clip.UserCopies("user copy");
        await Settle(e);
        Assert.Equal("user copy", _clip.Text);
    }

    [Fact]
    public async Task SensitiveOriginal_ClearedNotRestored()
    {
        _clip.Sensitive = true;
        _clip.Text = "hunter2";
        var e = Executor();
        await e.DeliverAsync(Item(), default);
        await Settle(e);
        Assert.Null(_clip.Text);
        Assert.Contains("clear", _clip.Log);
        Assert.DoesNotContain("restore", _clip.Log);
    }

    [Fact]
    public async Task UnsaveableOriginal_IsLeftAlone()
    {
        _clip.Text = null; // nothing saveable
        var e = new OutputExecutor(new UnsaveableClipboard(_clip), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await e.DeliverAsync(Item(), default);
        await Settle(e);
        Assert.Equal("dictated", _clip.Text);
        Assert.DoesNotContain("restore", _clip.Log);
    }

    private sealed class UnsaveableClipboard(FakeClipboard inner) : IClipboard
    {
        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => Task.FromResult(new SavedClipboard([], false, hadContent: true));
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct) => inner.SetTextAsync(text, excludeFromHistory, ct);
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    [Fact]
    public async Task CancelBeforePaste_RestoresTheOriginal()
    {
        using var cts = new CancellationTokenSource();
        var e = new OutputExecutor(new CancelOnSet(_clip, cts), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.DeliverAsync(Item(), cts.Token));
        Assert.Empty(_inj.Pastes);
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task CancelBeforePaste_SensitiveOriginal_ClearedNotRestored()
    {
        _clip.Sensitive = true;
        _clip.Text = "hunter2";
        using var cts = new CancellationTokenSource();
        var e = new OutputExecutor(new CancelOnSet(_clip, cts), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.DeliverAsync(Item(), cts.Token));
        Assert.Null(_clip.Text);
        Assert.DoesNotContain("restore", _clip.Log);
    }

    [Fact]
    public async Task CancelBeforePaste_UnsaveableOriginal_LeavesOurText()
    {
        using var cts = new CancellationTokenSource();
        var e = new OutputExecutor(new CancelOnSet(new UnsaveableClipboard(_clip), cts), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.DeliverAsync(Item(), cts.Token));
        Assert.Equal("dictated", _clip.Text);
        Assert.DoesNotContain("restore", _clip.Log);
    }

    [Fact]
    public async Task CancelBeforePaste_BusyClipboard_IsStillACancel_AndWipes()
    {
        using var cts = new CancellationTokenSource();
        var busy = new BusyRestore(_clip);
        var e = new OutputExecutor(new CancelOnSet(busy, cts), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.DeliverAsync(Item(), cts.Token));
        Assert.All(busy.Saved!.Items, i => Assert.All(i.Data, b => Assert.Equal(0, b)));
    }

    [Fact]
    public async Task SetFails_SavedBytesAreStillWiped()
    {
        var failing = new FailingSet(_clip);
        var e = new OutputExecutor(failing, _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await Assert.ThrowsAsync<OperationCanceledException>(() => e.DeliverAsync(Item(), default));
        Assert.All(failing.Saved!.Items, i => Assert.All(i.Data, b => Assert.Equal(0, b)));
        Assert.Empty(_inj.Pastes);
    }

    [Fact]
    public async Task PasteThrows_OriginalRestored_AndWiped()
    {
        var wiping = new RecordingSave(_clip);
        var e = new OutputExecutor(wiping, new ThrowingInjector(), _targets, _notes, _ => _start, _time, new RecordingLog());
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.DeliverAsync(Item(), default));
        Assert.Equal("user original", _clip.Text);
        Assert.All(wiping.Saved!.Items, i => Assert.All(i.Data, b => Assert.Equal(0, b)));
    }

    [Fact]
    public async Task IdleWaitThrows_StillRestores_AndWipes()
    {
        var wiping = new RecordingSave(_clip);
        var e = new OutputExecutor(wiping, _inj, new IdleThrows(), _notes, _ => _start, _time, new RecordingLog());
        await e.DeliverAsync(Item(), default);
        await e.PendingRestore;
        Assert.Equal("user original", _clip.Text);
        Assert.All(wiping.Saved!.Items, i => Assert.All(i.Data, b => Assert.Equal(0, b)));
    }

    [Fact]
    public async Task SetFailsAfterEmptying_OriginalRestored()
    {
        var e = new OutputExecutor(new EmptiesThenFails(_clip), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await Assert.ThrowsAsync<ClipboardChangedException>(() => e.DeliverAsync(Item(), default));
        Assert.Equal("user original", _clip.Text);
        Assert.Empty(_inj.Pastes);
    }

    [Fact]
    public async Task CopyFallback_SecondSetFails_StillCopied()
    {
        _targets.ForegroundHwnd = 999;
        var e = new OutputExecutor(new FailsSecondSet(_clip), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        Assert.Equal(DeliveryKind.Copied, await e.DeliverAsync(Item(), default));
        Assert.Equal("dictated", _clip.Text);
    }

    [Fact]
    public async Task CopyFallback_IgnoresALateCancel()
    {
        _targets.ForegroundHwnd = 999;
        using var cts = new CancellationTokenSource();
        var e = new OutputExecutor(new CancelAfterFirstSet(_clip, cts), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        // The cancel lands after the paste was abandoned for a copy: the copy still completes, kept in history.
        Assert.Equal(DeliveryKind.Copied, await e.DeliverAsync(Item(), cts.Token));
        Assert.True(_clip.InHistory);
    }

    [Fact]
    public async Task Flush_WaitsForADeliveryInFlight()
    {
        var gate = new TaskCompletionSource();
        var e = new OutputExecutor(new SlowSet(_clip, gate.Task), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        var delivery = e.DeliverAsync(Item(), default);
        var flush = e.FlushAsync();
        await Task.Delay(50);
        Assert.False(flush.IsCompleted);
        gate.SetResult();
        await delivery;
        await flush;
        Assert.Equal("user original", _clip.Text);
    }

    [Fact]
    public async Task CopyFallback_SecondSetEmptiesThenFails_OriginalRestored()
    {
        _targets.ForegroundHwnd = 999;
        var e = new OutputExecutor(new SecondSetEmptiesThenFails(_clip), _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await Assert.ThrowsAsync<ClipboardChangedException>(() => e.DeliverAsync(Item(), default));
        Assert.Equal("user original", _clip.Text);
        Assert.Empty(_notes.Shown);
    }

#pragma warning disable xUnit1031 // blocking is the scenario under test: AppHost's exit path does it
    [Fact]
    public void Flush_CompletesFromABlockedUiThread()
    {
        var gate = new TaskCompletionSource();
        var slow = new SlowSet(_clip, gate.Task);
        var e = new OutputExecutor(slow, _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        var delivery = Task.Run(() => e.DeliverAsync(Item(), default));
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NeverRunsPosts());
        try
        {
            // The flush must find the delivery in flight: one that hasn't started yet is refused (closed) instead,
            // which a fixed sleep didn't rule out on a slow CI runner.
            Assert.True(slow.Entered.Wait(TimeSpan.FromSeconds(5)));
            var flush = e.FlushAsync();
            _ = Task.Run(async () => { await Task.Delay(50); gate.SetResult(); });
            // AppHost blocks the UI thread like this on exit; continuations must not need that thread.
            Assert.True(flush.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        Assert.Equal("user original", _clip.Text);
        delivery.Wait();
    }
#pragma warning restore xUnit1031

    /// <summary>Like a UI thread blocked in <c>.Wait()</c>: anything posted to it never runs.</summary>
    private sealed class NeverRunsPosts : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    private sealed class SecondSetEmptiesThenFails(FakeClipboard inner) : IClipboard
    {
        private int _sets;
        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct)
        {
            if (++_sets == 1) return inner.SetTextAsync(text, excludeFromHistory, ct);
            inner.UserCopies(null!);
            throw new ClipboardChangedException(inner.Sequence, new IOException("SetClipboardData failed"));
        }
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    private sealed class EmptiesThenFails(FakeClipboard inner) : IClipboard
    {
        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct)
        {
            inner.UserCopies(null!); // EmptyClipboard ran, then SetClipboardData failed
            throw new ClipboardChangedException(inner.Sequence, new IOException("SetClipboardData failed"));
        }
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    private sealed class FailsSecondSet(FakeClipboard inner) : IClipboard
    {
        private int _sets;
        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct) =>
            ++_sets == 1 ? inner.SetTextAsync(text, excludeFromHistory, ct) : throw new IOException("clipboard is busy");
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    private sealed class CancelAfterFirstSet(FakeClipboard inner, CancellationTokenSource cts) : IClipboard
    {
        private int _sets;
        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct)
        {
            // The tap lands while the copy fallback waits for a busy clipboard.
            if (++_sets == 2) cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return inner.SetTextAsync(text, excludeFromHistory, ct);
        }
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    private sealed class SlowSet(FakeClipboard inner, Task release) : IClipboard
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The delivery has reached the set (so it holds the executor's gate).</summary>
        public Task Entered => _entered.Task;

        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
        public async Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct)
        {
            _entered.TrySetResult();
            await release;
            return await inner.SetTextAsync(text, excludeFromHistory, ct);
        }
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    private sealed class ThrowingInjector : IInjector
    {
        public Task WaitForModifiersUpAsync(CancellationToken ct) => Task.CompletedTask;
        public bool Paste(PasteChord chord) => throw new InvalidOperationException("SendInput failed");
    }

    private sealed class IdleThrows : ITargetReader
    {
        public TargetSnapshot Snapshot() => Word;
        public nint Foreground() => Word.Hwnd;
        public Task WaitForIdleAsync(nint hwnd, CancellationToken ct) => throw new InvalidOperationException("process gone");
    }

    /// <summary>A cancel tap while the guard is still retrying a busy clipboard.</summary>
    private sealed class FailingSet(FakeClipboard inner) : IClipboard
    {
        public SavedClipboard? Saved;
        public async Task<SavedClipboard> SaveAsync(CancellationToken ct) => Saved = await inner.SaveAsync(ct);
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct) => throw new OperationCanceledException();
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    /// <summary>The user taps cancel in the instant between our clipboard swap and the paste.</summary>
    private sealed class CancelOnSet(IClipboard inner, CancellationTokenSource cts) : IClipboard
    {
        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
        public async Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct)
        {
            var seq = await inner.SetTextAsync(text, excludeFromHistory, ct);
            cts.Cancel();
            return seq;
        }
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    private sealed class BusyRestore(FakeClipboard inner) : IClipboard
    {
        public SavedClipboard? Saved;
        public async Task<SavedClipboard> SaveAsync(CancellationToken ct) => Saved = await inner.SaveAsync(ct);
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct) => inner.SetTextAsync(text, excludeFromHistory, ct);
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => throw new IOException("clipboard is busy");
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }

    [Fact]
    public async Task Refuse_NeverTouchesTheClipboard()
    {
        _start = Word with { IsPassword = true };
        Assert.Equal(DeliveryKind.Refused, await Executor().DeliverAsync(Item(), default));
        Assert.Empty(_clip.Log);
        Assert.Equal(NoticeKind.PasswordRefused, Assert.Single(_notes.Shown).Kind);
    }

    [Fact]
    public async Task NoStartSnapshot_CopiesKeptInHistory()
    {
        _start = null;
        Assert.Equal(DeliveryKind.Copied, await Executor().DeliverAsync(Item(), default));
        Assert.Equal(["set-kept"], _clip.Log);
    }

    [Fact]
    public async Task Mintty_PastesWithShiftInsert()
    {
        var mintty = Word with { WindowClass = "mintty", ProcessName = "mintty" };
        _start = mintty;
        _targets.Now = mintty;
        await Executor().DeliverAsync(Item(), default);
        Assert.Equal([PasteChord.ShiftInsert], _inj.Pastes);
    }

    [Fact]
    public async Task SavedBytes_AreWipedAfterRestore()
    {
        var wiping = new RecordingSave(_clip);
        var e = new OutputExecutor(wiping, _inj, _targets, _notes, _ => _start, _time, new RecordingLog());
        await e.DeliverAsync(Item(), default);
        await Settle(e);
        Assert.All(wiping.Saved!.Items, i => Assert.All(i.Data, b => Assert.Equal(0, b)));
    }

    private sealed class RecordingSave(FakeClipboard inner) : IClipboard
    {
        public SavedClipboard? Saved;
        public async Task<SavedClipboard> SaveAsync(CancellationToken ct) => Saved = await inner.SaveAsync(ct);
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct) => inner.SetTextAsync(text, excludeFromHistory, ct);
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => inner.RestoreAsync(saved, ourSequence, ct);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => inner.ClearIfOursAsync(ourSequence, ct);
    }
}
