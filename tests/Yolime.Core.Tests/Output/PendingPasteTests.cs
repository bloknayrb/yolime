using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Output;

namespace Yolime.Core.Tests.Output;

public class PendingPasteTests
{
    private static readonly TargetSnapshot ReadingPane = new(100, 1, "OUTLOOK", "rctrl_renwnd32", "1.1", "Pane", false, false, false);
    private static readonly TargetSnapshot Compose = ReadingPane with { FocusRuntimeId = "1.2", FocusClass = "Edit", IsEditable = true };

    private sealed class Clip : IClipboard
    {
        public uint Sequence = 1;
        public string? Text = "user original";
        public List<string> Log { get; } = [];
        public Task<SavedClipboard> SaveAsync(CancellationToken ct) => Task.FromResult(new SavedClipboard([(13u, [1])], false, true));
        public Task<uint> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct)
        {
            Log.Add(excludeFromHistory ? "set-transient" : "set-kept");
            Text = text;
            return Task.FromResult(++Sequence);
        }
        public Task<uint> RestoreAsync(SavedClipboard saved, uint ourSequence, CancellationToken ct) => Task.FromResult(0u);
        public Task<uint> ClearIfOursAsync(uint ourSequence, CancellationToken ct) => Task.FromResult(0u);
    }

    private sealed class Inj : IInjector
    {
        public int Pastes;
        public bool Stuck;
        public Task WaitForModifiersUpAsync(CancellationToken ct) => Stuck ? throw new TimeoutException() : Task.CompletedTask;
        public bool Paste(PasteChord chord)
        {
            Pastes++;
            return true;
        }
    }

    private sealed class Targets : ITargetReader
    {
        public TargetSnapshot Now = ReadingPane;
        public TargetSnapshot Snapshot() => Now;
        public nint Foreground() => Now.Hwnd;
        public Task WaitForIdleAsync(nint hwnd, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Notes : INoticeSink
    {
        public List<NoticeKind> Shown { get; } = [];
        public void Show(Notice notice) => Shown.Add(notice.Kind);
    }

    private sealed class Waiter(Func<TargetSnapshot?> next, Targets targets) : IEditableFocusWaiter
    {
        public int Asked;
        public int ForPid;
        public Task<TargetSnapshot?> WaitAsync(int processId, TimeSpan limit, CancellationToken ct)
        {
            Asked++;
            ForPid = processId;
            var found = next();
            if (found is not null) targets.Now = found;
            return Task.FromResult(found);
        }
    }

    private readonly Clip _clip = new();
    private readonly Inj _inj = new();
    private readonly Targets _targets = new();
    private readonly Notes _notes = new();

    private OutputExecutor Executor(IEditableFocusWaiter waiter) =>
        new(_clip, _inj, _targets, _notes, _ => ReadingPane, new FakeTimeProvider(), new RecordingLog(), focusWaiter: waiter);

    private static OutputItem Reply() => new("reply text", DeliveryIntent.Paste, false, Mode.Reply, Guid.NewGuid());

    [Fact]
    public async Task AReplyFromAReadOnlyPane_WaitsForTheUserToClickAField_ThenPastesThere()
    {
        var waiter = new Waiter(() => Compose, _targets);
        var e = Executor(waiter);
        int? waitingSeconds = null;
        e.PendingPaste += s => waitingSeconds ??= s;

        Assert.Equal(DeliveryKind.Pasted, await e.DeliverAsync(Reply(), default));
        Assert.Equal(1, waiter.Asked);
        Assert.Equal(1, waiter.ForPid); // only the original app's fields count
        Assert.Equal(10, waitingSeconds);
        Assert.Equal(1, _inj.Pastes);
    }

    [Fact]
    public async Task AClickWithCtrlStillHeld_Copies_NeverLosesTheReply()
    {
        _inj.Stuck = true;
        var e = Executor(new Waiter(() => Compose, _targets));
        Assert.Equal(DeliveryKind.Copied, await e.DeliverAsync(Reply(), default));
        Assert.Equal("reply text", _clip.Text);
    }

    [Fact]
    public async Task AHoldKeyTapWhileWaiting_Copies()
    {
        using var cts = new CancellationTokenSource();
        var waiter = new BlockingWaiter();
        var delivery = Executor(waiter).DeliverAsync(Reply(), cts.Token);
        await waiter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        Assert.Equal(DeliveryKind.Copied, await delivery);
        Assert.Equal("reply text", _clip.Text);
        Assert.Equal(0, _inj.Pastes);
    }

    [Fact]
    public async Task AQuietRun_CopiesWithoutTheExecutorsNotice()
    {
        var e = Executor(new Waiter(() => null, _targets));
        var quiet = Reply() with { QuietCopy = true };
        Assert.Equal(DeliveryKind.Copied, await e.DeliverAsync(quiet, default));
        Assert.Empty(_notes.Shown);
    }

    [Fact]
    public async Task AfterTheExitFlush_NothingIsPasted()
    {
        var e = Executor(new Waiter(() => Compose, _targets));
        await e.FlushAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.DeliverAsync(Reply(), default));
        Assert.Equal(0, _inj.Pastes);
    }

    private sealed class BlockingWaiter : IEditableFocusWaiter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TargetSnapshot?> WaitAsync(int processId, TimeSpan limit, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    [Fact]
    public async Task NoClickInTime_Copies_AsBefore()
    {
        var e = Executor(new Waiter(() => null, _targets));
        Assert.Equal(DeliveryKind.Copied, await e.DeliverAsync(Reply(), default));
        Assert.Equal(0, _inj.Pastes);
        Assert.Contains(NoticeKind.Copied, _notes.Shown);
    }

    [Fact]
    public async Task AClickIntoAPasswordField_IsRefused_NotPasted()
    {
        var e = Executor(new Waiter(() => Compose with { IsPassword = true }, _targets));
        Assert.NotEqual(DeliveryKind.Pasted, await e.DeliverAsync(Reply(), default));
        Assert.Equal(0, _inj.Pastes);
    }

    [Fact]
    public async Task OnlyReplyWaits_DictateCopiesAtOnce()
    {
        var waiter = new Waiter(() => Compose, _targets);
        var dictate = new OutputItem("x", DeliveryIntent.Paste, false, Mode.Dictate, Guid.NewGuid());
        Assert.Equal(DeliveryKind.Copied, await Executor(waiter).DeliverAsync(dictate, default));
        Assert.Equal(0, waiter.Asked);
    }

    [Fact]
    public async Task UseRaw_CopiesTheTranscript_KeptInClipboardHistory_AndSaysSo()
    {
        var e = Executor(new Waiter(() => null, _targets));
        await e.CopyForUserAsync("the raw words", NoticeKind.RawCopied, default);
        Assert.Equal("the raw words", _clip.Text);
        Assert.Equal(["set-kept"], _clip.Log);
        Assert.Equal([NoticeKind.RawCopied], _notes.Shown);
    }
}
