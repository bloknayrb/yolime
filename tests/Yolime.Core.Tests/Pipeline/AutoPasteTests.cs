using Yolime.Core.Llm;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Pipeline;

/// <summary>Paste automatically off: unattended results are copied, explicit pastes (the preview's Insert) still paste.</summary>
public sealed class AutoPasteTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public AutoPasteTests() => _rig.Settings = _rig.Settings with { AutoPaste = false };

    public void Dispose() => _rig.Dispose();

    private static RunRequest Request(Mode mode, bool preview = false) =>
        new(PipelineRig.Speech(), mode, "slack", Task.FromResult(new RunIntent(mode, preview)));

    [Fact]
    public void IsOnByDefault() => Assert.True(new Yolime.Core.Settings.YolimeSettings().AutoPaste);

    [Fact]
    public async Task Off_ARawRun_IsCopied_Quietly()
    {
        var result = await _rig.Build().RunAsync(Request(Mode.Raw), CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.ClipboardOnly, item.Intent);
        Assert.True(item.QuietCopy); // the Done card says "Copied": no second notice
        Assert.False(item.PreviewRequested);
        Assert.False(item.KeepOutOfClipboardHistory);
    }

    [Theory]
    [InlineData(Mode.Dictate)]
    [InlineData(Mode.Prompt)]
    [InlineData(Mode.Reply)]
    public async Task Off_ARewrittenRun_IsCopied(Mode mode)
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        _rig.Rewriter = new FakeRewriter(_ => ["Done."]);
        var result = await _rig.Build().RunAsync(Request(mode), CancellationToken.None);

        Assert.True(result.Rewritten);
        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.ClipboardOnly, item.Intent);
        Assert.True(item.QuietCopy);
    }

    [Theory]
    [InlineData(Mode.Dictate)] // its fallback would otherwise paste the raw transcript
    [InlineData(Mode.Prompt)]
    [InlineData(Mode.Reply)]
    public async Task Off_AFallback_IsCopied_Quietly(Mode mode)
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        var result = await _rig.Build().RunAsync(Request(mode), CancellationToken.None);

        Assert.Equal(RunOutcome.CompletedWithFallback, result.Outcome);
        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.ClipboardOnly, item.Intent);
        Assert.True(item.QuietCopy);
    }

    [Fact]
    public async Task Off_TheShiftPreview_StillPastes_OnInsert()
    {
        await _rig.Build().RunAsync(Request(Mode.Raw, preview: true), CancellationToken.None);

        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.True(item.PreviewRequested);
        Assert.Equal(DeliveryIntent.Paste, item.Intent);
        Assert.False(item.QuietCopy);
    }

    [Fact]
    public async Task Off_AlwaysPreview_StillPastes_EvenAfterADictateFallback()
    {
        _rig.Settings = _rig.Settings with { PreviewModes = ["Dictate"] };
        _rig.Stt.Text = PipelineRig.LongTranscript;
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        await _rig.Build().RunAsync(Request(Mode.Dictate), CancellationToken.None);

        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.True(item.PreviewRequested);
        Assert.Equal(DeliveryIntent.Paste, item.Intent);
    }

    [Fact]
    public async Task Off_WithHistoryPaused_TheCopyStaysOutOfClipboardHistory()
    {
        _rig.Settings = _rig.Settings with { HistoryPaused = true };
        await _rig.Build().RunAsync(Request(Mode.Raw), CancellationToken.None);

        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.ClipboardOnly, item.Intent);
        Assert.True(item.KeepOutOfClipboardHistory);
    }

    [Theory]
    [InlineData(Mode.Prompt)] // already a copy by fallback: still kept out while paused
    [InlineData(Mode.Reply)]
    [InlineData(Mode.Dictate)]
    public async Task Off_WithHistoryPaused_AFallbackCopyStaysOutOfClipboardHistory(Mode mode)
    {
        _rig.Settings = _rig.Settings with { HistoryPaused = true };
        _rig.Stt.Text = PipelineRig.LongTranscript;
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        await _rig.Build().RunAsync(Request(mode), CancellationToken.None);

        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.ClipboardOnly, item.Intent);
        Assert.True(item.KeepOutOfClipboardHistory);
    }

    [Fact]
    public async Task On_WithHistoryPaused_APasteIsUnchanged()
    {
        _rig.Settings = _rig.Settings with { AutoPaste = true, HistoryPaused = true };
        await _rig.Build().RunAsync(Request(Mode.Raw), CancellationToken.None);

        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.Paste, item.Intent);
        Assert.False(item.QuietCopy);
        Assert.False(item.KeepOutOfClipboardHistory);
    }

    [Fact]
    public async Task Off_ARetry_IsCopied()
    {
        _rig.Stt.Throw = new SttException(SttError.Network);
        var failed = await _rig.Build().RunAsync(Request(Mode.Raw), CancellationToken.None);
        Assert.Equal(RunOutcome.SttFailed, failed.Outcome);
        _rig.Stt.Throw = null;

        var result = await _rig.Build().RetryAsync(failed.HistoryId!.Value, null, null, CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.ClipboardOnly, item.Intent);
    }

    [Fact]
    public async Task Off_TheRunCarriesTheTargetsPasteKey_ForTheDoneCard()
    {
        // Each run is a quiet copy: the Done card is the only hint, and in Warp it must say Ctrl+Shift+V.
        _rig.Sink.Result = Yolime.Core.History.DeliveryKind.Copied;
        _rig.Sink.Chord = PasteChord.CtrlShiftV;
        var result = await _rig.Build().RunAsync(Request(Mode.Raw), CancellationToken.None);

        Assert.Equal(PasteChord.CtrlShiftV, result.PasteChord);
        Assert.Null(Yolime.Core.Feedback.Notices.For(result));
    }

    [Fact]
    public async Task Off_AFallbacksNotice_NamesTheTargetsPasteKey()
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        _rig.Sink.Result = Yolime.Core.History.DeliveryKind.Copied;
        _rig.Sink.Chord = PasteChord.ShiftInsert;
        var result = await _rig.Build().RunAsync(Request(Mode.Prompt), CancellationToken.None);

        var notice = Yolime.Core.Feedback.Notices.For(result)!;
        Assert.Equal(PasteChord.ShiftInsert, notice.Chord);
        Assert.Contains("press\u00a0Shift+Insert", Yolime.Core.Feedback.Notices.Text(notice));
    }
}
