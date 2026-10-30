using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Output;

namespace Yolime.Core.Tests.Output;

public class PreviewTests
{
    private static readonly TargetSnapshot Word = new(100, 1, "WINWORD", "OpusApp", "42.1", "_WwG", false, true, false);

    private sealed class Clip : IClipboard
    {
        public uint Sequence = 1;
        public string? Text = "user original";
        public List<string> Log { get; } = [];
        public Task<SavedClipboard> SaveAsync(CancellationToken ct)
        {
            Log.Add("save");
            return Task.FromResult(new SavedClipboard([(13u, System.Text.Encoding.UTF8.GetBytes(Text ?? ""))], false, Text is not null));
        }
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
        public Task WaitForModifiersUpAsync(CancellationToken ct) => Task.CompletedTask;
        public bool Paste(PasteChord chord)
        {
            Pastes++;
            return true;
        }
    }

    private sealed class Targets : ITargetReader
    {
        public TargetSnapshot Now = Word;
        public TargetSnapshot Snapshot() => Now;
        public nint Foreground() => Now.Hwnd;
        public Task WaitForIdleAsync(nint hwnd, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Notes : INoticeSink
    {
        public List<NoticeKind> Shown { get; } = [];
        public void Show(Notice notice) => Shown.Add(notice.Kind);
    }

    /// <summary>Answers with a fixed choice, optionally running something "while the card is up" first.</summary>
    private sealed class Prompt(PreviewChoice choice, Action? whileShown = null) : IPreviewPrompt
    {
        public List<string> Asked { get; } = [];
        public TaskCompletionSource? Hold;

        public async Task<PreviewChoice> AskAsync(string text, CancellationToken ct)
        {
            Asked.Add(text);
            whileShown?.Invoke();
            if (Hold is { } h) await h.Task.WaitAsync(ct);
            return choice;
        }
    }

    private readonly Clip _clip = new();
    private readonly Inj _inj = new();
    private readonly Targets _targets = new();
    private readonly Notes _notes = new();
    private readonly FakeTimeProvider _time = new();

    private OutputExecutor Executor(IPreviewPrompt? prompt) =>
        new(_clip, _inj, _targets, _notes, _ => Word, _time, new RecordingLog(), preview: prompt);

    private static OutputItem Item(bool preview = true) => new("dictated text", DeliveryIntent.Paste, preview, Mode.Prompt, Guid.NewGuid());

    [Fact]
    public async Task Insert_PastesAfterTheCard()
    {
        var prompt = new Prompt(PreviewChoice.Insert);
        Assert.Equal(DeliveryKind.Pasted, await Executor(prompt).DeliverAsync(Item(), default));
        Assert.Equal(["dictated text"], prompt.Asked);
        Assert.Equal(1, _inj.Pastes);
    }

    [Fact]
    public async Task Insert_ReplansAgainstTheTargetAsItIsNow()
    {
        // While the card was up the user moved to a password field: Insert must not type into it.
        var prompt = new Prompt(PreviewChoice.Insert, () => _targets.Now = Word with { FocusRuntimeId = "99", IsPassword = true });
        var kind = await Executor(prompt).DeliverAsync(Item(), default);
        Assert.NotEqual(DeliveryKind.Pasted, kind);
        Assert.Equal(0, _inj.Pastes);
    }

    [Fact]
    public async Task Discard_TouchesNothing()
    {
        Assert.Equal(DeliveryKind.Discarded, await Executor(new Prompt(PreviewChoice.Discard)).DeliverAsync(Item(), default));
        Assert.Empty(_clip.Log);
        Assert.Equal(0, _inj.Pastes);
        Assert.Empty(_notes.Shown);
    }

    [Fact]
    public async Task Expired_KeepsItInHistoryOnly_AndSaysSo()
    {
        Assert.Equal(DeliveryKind.Discarded, await Executor(new Prompt(PreviewChoice.Expired)).DeliverAsync(Item(), default));
        Assert.Empty(_clip.Log); // never an unrequested copy
        Assert.Equal([NoticeKind.PreviewKept], _notes.Shown);
    }

    [Fact]
    public async Task Copy_PutsItOnTheClipboard_KeptInHistory()
    {
        Assert.Equal(DeliveryKind.Copied, await Executor(new Prompt(PreviewChoice.Copy)).DeliverAsync(Item(), default));
        Assert.Equal(["set-kept"], _clip.Log);
        Assert.Equal(0, _inj.Pastes);
    }

    [Fact]
    public async Task APasswordTarget_IsRefused_WithoutAPreview()
    {
        var prompt = new Prompt(PreviewChoice.Insert);
        var e = new OutputExecutor(_clip, _inj, _targets, _notes, _ => Word with { IsPassword = true }, _time, new RecordingLog(), preview: prompt);
        Assert.Equal(DeliveryKind.Refused, await e.DeliverAsync(Item(), default));
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task NoPreviewRequested_NeverAsks()
    {
        var prompt = new Prompt(PreviewChoice.Discard);
        Assert.Equal(DeliveryKind.Pasted, await Executor(prompt).DeliverAsync(Item(preview: false), default));
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task ExitFlush_DoesNotWaitForAnOpenCard_AndEndsIt()
    {
        var prompt = new Prompt(PreviewChoice.Insert) { Hold = new TaskCompletionSource() };
        var e = Executor(prompt);
        var delivery = e.DeliverAsync(Item(), default);
        await Task.Delay(50);
        var flush = e.FlushAsync();
        Assert.Same(flush, await Task.WhenAny(flush, Task.Delay(2000))); // the card isn't holding the executor's lock
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.Equal(0, _inj.Pastes);
    }

    [Fact]
    public async Task ACancelTap_EndsTheCard_AsACancel()
    {
        using var cts = new CancellationTokenSource();
        var prompt = new Prompt(PreviewChoice.Insert) { Hold = new TaskCompletionSource() };
        var delivery = Executor(prompt).DeliverAsync(Item(), cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.Empty(_clip.Log);
    }
}
