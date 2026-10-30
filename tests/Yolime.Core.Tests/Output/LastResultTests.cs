using Microsoft.Extensions.Time.Testing;
using Yolime.Core.History;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Output;

public class LastResultTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private bool _paused;
    private readonly LastResult _last;

    public LastResultTests() => _last = new LastResult(_time, () => _paused);

    private static RunResult Run(string? output, RunOutcome outcome = RunOutcome.Completed, DeliveryKind delivery = DeliveryKind.Pasted, Mode mode = Mode.Dictate) =>
        new() { RunId = Guid.NewGuid(), Outcome = outcome, Mode = mode, Output = output, Delivery = delivery };

    [Fact]
    public void ADeliveredRun_IsKept_ForHalfAnHour()
    {
        _last.Record(Run("hello there", mode: Mode.Prompt));
        Assert.True(_last.TryGet(out var text, out var mode));
        Assert.Equal(("hello there", Mode.Prompt), (text, mode));

        _time.Advance(LastResult.Keep + TimeSpan.FromSeconds(1));
        Assert.False(_last.TryGet(out _, out _));
    }

    [Theory]
    [InlineData(RunOutcome.CompletedWithFallback, DeliveryKind.Copied)]
    [InlineData(RunOutcome.Completed, DeliveryKind.PendingPaste)]
    public void FallbacksAndCopies_Count(RunOutcome outcome, DeliveryKind delivery)
    {
        _last.Record(Run("text", outcome, delivery));
        Assert.True(_last.TryGet(out _, out _));
    }

    [Theory]
    [InlineData(DeliveryKind.Refused)]
    [InlineData(DeliveryKind.Discarded)]
    public void ARefusedOrDiscardedRun_ClearsIt(DeliveryKind delivery)
    {
        _last.Record(Run("first"));
        _last.Record(Run("second", delivery: delivery));
        Assert.False(_last.TryGet(out _, out _));
    }

    [Theory]
    [InlineData(RunOutcome.NoSpeech)]
    [InlineData(RunOutcome.SttFailed)]
    [InlineData(RunOutcome.Cancelled)]
    public void ARunWithNoOutput_LeavesThePreviousOne(RunOutcome outcome)
    {
        _last.Record(Run("first"));
        _last.Record(Run(null, outcome, DeliveryKind.None));
        Assert.True(_last.TryGet(out var text, out _));
        Assert.Equal("first", text);
    }

    [Fact]
    public void WhileHistoryIsPaused_NothingIsKeptOrGiven()
    {
        _last.Record(Run("before"));
        _paused = true;
        Assert.False(_last.TryGet(out _, out _));
        _last.Record(Run("during"));
        _paused = false;
        Assert.True(_last.TryGet(out var text, out _));
        Assert.Equal("before", text);
    }

    [Fact]
    public void ARunThatStartedBeforeAClear_IsNotKept()
    {
        // Clear history waits for the run in progress, but the run's result is recorded after the pipeline lets go:
        // it must not bring back what was just cleared.
        var started = _last.Generation;
        _last.Clear();
        _last.Record(Run("from before the clear"), started);
        Assert.False(_last.TryGet(out _, out _));

        _last.Record(Run("after"), _last.Generation);
        Assert.True(_last.TryGet(out _, out _));
    }

    [Fact]
    public void Clear_ForgetsIt()
    {
        _last.Record(Run("text"));
        _last.Clear();
        Assert.False(_last.TryGet(out _, out _));
    }

    [Fact]
    public void DeletingTheRunInHistory_ForgetsItsText()
    {
        _last.Record(Run("delete me") with { HistoryId = 5 });
        _last.ForgetRuns([6, 7]);
        Assert.True(_last.TryGet(out _, out _));
        _last.ForgetRuns([5]);
        Assert.False(_last.TryGet(out _, out _));
    }

    [Fact]
    public void ARunFinishingAfterItsDelete_DoesntComeBack()
    {
        var started = _last.Generation;
        _last.Record(Run("older") with { HistoryId = 1 });
        _last.ForgetRuns([1]);
        _last.Record(Run("deleted while finishing") with { HistoryId = 2 }, started);
        Assert.False(_last.TryGet(out _, out _));
    }
}
