using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Pipeline;

/// <summary>With history paused, yolime keeps nothing: no row and no WAV, whatever the run's outcome.</summary>
public sealed class HistoryPausedTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public HistoryPausedTests() => _rig.Settings = _rig.Settings with { HistoryPaused = true };

    public void Dispose() => _rig.Dispose();

    private void AssertNothingKept(Yolime.Core.Pipeline.RunResult result)
    {
        Assert.Null(result.HistoryId);
        Assert.Empty(_rig.History.Recent(10));
        Assert.Empty(_rig.Pending.List());
    }

    [Fact]
    public async Task ADeliveredRun_KeepsNothing()
    {
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal("hello world", Assert.Single(_rig.Sink.Delivered).Text);
        AssertNothingKept(result);
    }

    [Fact]
    public async Task AFailedTranscription_KeepsNothing()
    {
        _rig.Stt.Throw = new SttException(SttError.Server);
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        Assert.Equal(RunOutcome.SttFailed, result.Outcome);
        AssertNothingKept(result);
    }

    [Fact]
    public async Task Silence_KeepsNothing()
    {
        var result = await _rig.Build().RunAsync(PipelineRig.Request(new FileAudioSource(Fixtures.Audio("noise-floor-5s.wav"))), CancellationToken.None);
        Assert.Equal(RunOutcome.NoSpeech, result.Outcome);
        AssertNothingKept(result);
    }

    [Fact]
    public async Task AnUnexpectedFailure_KeepsNothing()
    {
        _rig.Sink.Throw = new InvalidOperationException("bug");
        var result = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        AssertNothingKept(result);
    }
}

public sealed class ClearHistoryTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Clear_WaitsForTheRunInProgress_ThenRemovesEverything()
    {
        var pipeline = _rig.Build();
        _rig.Stt.Throw = new SttException(SttError.Server); // leaves a row and a WAV behind
        await pipeline.RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        Assert.Single(_rig.Pending.List());
        _rig.Stt.Throw = null;

        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        _rig.Sink.OnDeliver = _ => { entered.TrySetResult(); release.Task.Wait(5000); };
        var running = Task.Run(() => pipeline.RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var last = new Yolime.Core.Output.LastResult(_rig.Time, () => false);
        last.Record(new Yolime.Core.Pipeline.RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.Completed, Mode = Mode.Dictate, Output = "x", Delivery = DeliveryKind.Pasted });
        var clearing = pipeline.ClearHistoryAsync(last, CancellationToken.None);
        await Task.Delay(150);
        Assert.False(clearing.IsCompleted); // the run in progress would write its row back afterwards
        _rig.Time.Advance(TimeSpan.FromSeconds(1));

        release.SetResult();
        await running;
        for (var i = 0; i < 100 && !clearing.IsCompleted; i++)
        {
            _rig.Time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }
        Assert.True(await clearing);
        Assert.Empty(_rig.History.Recent(10));
        Assert.Empty(_rig.Pending.List());
        Assert.False(last.TryGet(out _, out _));
    }
}
