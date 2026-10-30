using Yolime.Core.History;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Pipeline;

/// <summary>PR6: re-running a kept transcript in another mode, and deleting runs from History.</summary>
public sealed class RerunTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private long Source(Mode mode = Mode.Reply, string? context = "Lunch Friday?") => _rig.History.Insert(new HistoryRow
    {
        RunId = Guid.NewGuid(),
        StartedUtc = _rig.Time.GetUtcNow().AddHours(-3),
        Mode = mode,
        ProcessName = "slack",
        RawText = "sounds good see you then",
        Context = context,
        ContextSource = context is null ? null : ContextSource.Selection,
        Output = "Sounds good, see you then!",
        Outcome = RunOutcome.Completed,
        Delivery = DeliveryKind.Pasted,
    });

    [Fact]
    public async Task AReRun_IsANewRow_LinkedToItsSource_WhichStaysAsItWas()
    {
        var source = Source();
        var before = _rig.History.Get(source)!;

        var result = await _rig.Build().RerunAsync(source, Mode.Raw, null, CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        var rerun = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.NotEqual(source, rerun.Id);
        Assert.Equal(source, rerun.RerunOf);
        Assert.Equal(Mode.Raw, rerun.Mode);
        Assert.Equal("sounds good see you then", rerun.Output); // Raw is the transcript as spoken
        Assert.Equal(before.StartedUtc, rerun.StartedUtc); // retention counts from the original run
        var after = _rig.History.Get(source)!;
        Assert.Equal((before.Mode, before.Output, before.Outcome, before.Delivery, before.Context, before.RerunOf),
            (after.Mode, after.Output, after.Outcome, after.Delivery, after.Context, after.RerunOf));
    }

    [Fact]
    public async Task AReRun_GoesToTheClipboard_WithNoPreview()
    {
        _rig.Settings = _rig.Settings with { PreviewModes = ["Raw"] };
        var result = await _rig.Build().RerunAsync(Source(), Mode.Raw, null, CancellationToken.None);
        var item = Assert.Single(_rig.Sink.Delivered);
        Assert.Equal(DeliveryIntent.ClipboardOnly, item.Intent);
        Assert.False(item.PreviewRequested);
        Assert.Equal(result.RunId, item.RunId);
    }

    [Theory]
    [InlineData(Mode.Raw, false)]
    [InlineData(Mode.Dictate, false)]
    [InlineData(Mode.Prompt, true)]
    public async Task Context_IsCopiedOnlyIntoModesThatSendIt(Mode mode, bool keepsContext)
    {
        _rig.Rewriter = new FakeRewriter(_ => ["Rewritten."]);
        var result = await _rig.Build().RerunAsync(Source(), mode, null, CancellationToken.None);
        var rerun = _rig.History.Get(result.HistoryId!.Value)!;
        Assert.Equal(keepsContext, rerun.Context is not null);
    }

    [Fact]
    public async Task AReRunOfAReRun_PointsAtTheOriginal()
    {
        var source = Source();
        var first = await _rig.Build().RerunAsync(source, Mode.Raw, null, CancellationToken.None);
        var second = await _rig.Build().RerunAsync(first.HistoryId!.Value, Mode.Dictate, null, CancellationToken.None);
        Assert.Equal(source, _rig.History.Get(second.HistoryId!.Value)!.RerunOf);
    }

    [Fact]
    public async Task WhileHistoryIsPaused_NothingIsReRun()
    {
        var source = Source();
        _rig.Settings = _rig.Settings with { HistoryPaused = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Build().RerunAsync(source, Mode.Raw, null, CancellationToken.None));
        Assert.Single(_rig.History.Recent(10));
    }

    [Fact]
    public async Task ARunWithNoTranscript_CantBeReRun()
    {
        var id = _rig.History.Insert(new HistoryRow { RunId = Guid.NewGuid(), StartedUtc = _rig.Time.GetUtcNow(), Mode = Mode.Dictate, Outcome = RunOutcome.SttFailed });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Build().RerunAsync(id, Mode.Raw, null, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_TakesTheRunAndItsReRuns_AndSaysWhich()
    {
        var pipeline = _rig.Build();
        var source = Source();
        var rerun = (await pipeline.RerunAsync(source, Mode.Raw, null, CancellationToken.None)).HistoryId!.Value;
        var other = Source();

        var deleted = await pipeline.DeleteRunAsync(source, CancellationToken.None);

        Assert.Equal([source, rerun], deleted.Order());
        Assert.NotNull(_rig.History.Get(other));
    }
}
