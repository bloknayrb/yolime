using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Pipeline;

public sealed class RetryTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private async Task<long> FailedRun()
    {
        _rig.Stt.Throw = new SttException(SttError.Network);
        var failed = await _rig.Build().RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        Assert.Equal(RunOutcome.SttFailed, failed.Outcome);
        _rig.Stt.Throw = null;
        return failed.HistoryId!.Value;
    }

    [Fact]
    public async Task Retry_AfterSttFailure_SameRowSameWav_DeletesWavOnSuccess()
    {
        var id = await FailedRun();
        var wav = _rig.Pending.List().Single();

        var result = await _rig.Build().RetryAsync(id, null, null, CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(id, result.HistoryId);
        Assert.Single(_rig.History.Recent(10));
        var row = _rig.History.Get(id)!;
        Assert.Equal("hello world", row.RawText);
        Assert.Equal(RunOutcome.Completed, row.Outcome);
        Assert.Null(row.SttError);
        Assert.Empty(_rig.Pending.List());
        Assert.False(File.Exists(_rig.Pending.PathOf(wav)));
        Assert.Equal(WavFile.ReadPcm(Fixtures.Audio("speech-short.wav")).Length * 2, _rig.Stt.PushedBytes);
    }

    [Fact]
    public async Task Retry_AsOtherMode_UsesThatMode()
    {
        var id = await FailedRun();
        var result = await _rig.Build().RetryAsync(id, Mode.Dictate, null, CancellationToken.None);

        Assert.Equal(Mode.Dictate, result.Mode);
        Assert.Equal(Mode.Dictate, _rig.History.Get(id)!.Mode);
    }

    [Fact]
    public async Task Retry_NoSpeech_SkipsGate_SendsAnyway()
    {
        var first = await _rig.Build().RunAsync(PipelineRig.Request(new FileAudioSource(Fixtures.Audio("noise-floor-5s.wav"))), CancellationToken.None);
        Assert.Equal(RunOutcome.NoSpeech, first.Outcome);

        var retry = await _rig.Build().RetryAsync(first.HistoryId!.Value, null, null, CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, retry.Outcome);
        Assert.Equal(1, _rig.Stt.Completions);
    }

    [Fact]
    public async Task Retry_Cancelled_KeepsPreviousOutcomeAndWav()
    {
        var id = await FailedRun();
        _rig.Stt.WaitForCancel = true;
        _rig.Stt.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var run = _rig.Build().RetryAsync(id, null, null, cts.Token);
        await _rig.Stt.Entered.Task;
        cts.Cancel();

        Assert.Equal(RunOutcome.Cancelled, (await run).Outcome);
        var row = _rig.History.Get(id)!;
        Assert.Equal(RunOutcome.SttFailed, row.Outcome);
        Assert.Equal(SttError.Network, row.SttError);
        Assert.Single(_rig.Pending.List());
    }

    [Fact]
    public async Task Retry_WavMissing_ThrowsInvalidOperation_RowKeepsOutcome_AudioExpired()
    {
        var id = await FailedRun();
        _rig.Pending.Delete(_rig.Pending.List().Single());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Build().RetryAsync(id, null, null, CancellationToken.None));
        var row = _rig.History.Get(id)!;
        Assert.Equal(RunOutcome.SttFailed, row.Outcome);
        Assert.True(row.AudioExpired);
        Assert.Null(_rig.History.LatestRetryable());
    }

    [Fact]
    public async Task Retry_ExpiredAudio_ThrowsInvalidOperation()
    {
        var id = await FailedRun();
        _rig.History.MarkAudioExpired(_rig.Pending.List());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Build().RetryAsync(id, null, null, CancellationToken.None));
    }
}
