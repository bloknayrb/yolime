using Yolime.Core.History;
using Yolime.Core.Settings;
using Yolime.Core.Startup;
using Yolime.Core.Tests.Pipeline;

namespace Yolime.Core.Tests.Startup;

public sealed class HousekeepingTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private HistoryRow Row(RunOutcome outcome, string? wav = null, DeliveryKind delivery = DeliveryKind.None) => new()
    {
        RunId = Guid.NewGuid(),
        StartedUtc = _rig.Time.GetUtcNow(),
        Mode = Mode.Dictate,
        Outcome = outcome,
        PendingAudioFile = wav,
        Delivery = delivery,
    };

    [Fact]
    public async Task Startup_MarksInProgressInterrupted_AndRetryable()
    {
        await using (var rec = _rig.Pending.Begin(Guid.NewGuid())) await rec.FinaliseAsync();
        var id = _rig.History.Insert(Row(RunOutcome.InProgress, _rig.Pending.List().Single()));

        Housekeeping.Run(_rig.History, _rig.Pending, new YolimeSettings());

        Assert.Equal(RunOutcome.Interrupted, _rig.History.Get(id)!.Outcome);
        Assert.Equal(id, _rig.History.LatestRetryable()!.Id);
    }

    [Fact]
    public async Task Startup_APausedRunsRecording_IsDeleted_NotAdopted()
    {
        // History was paused: a crash mid-run mustn't turn that recording into a retryable history row.
        await using (var rec = _rig.Pending.Begin(Guid.NewGuid(), historyPaused: true)) await rec.FinaliseAsync();

        Housekeeping.Run(_rig.History, _rig.Pending, new YolimeSettings());

        Assert.Empty(_rig.History.Recent(10));
        Assert.Empty(_rig.Pending.List());
    }

    [Fact]
    public async Task Startup_OrphanWav_GetsInterruptedRow()
    {
        var runId = Guid.NewGuid();
        await using (var rec = _rig.Pending.Begin(runId)) await rec.FinaliseAsync();

        Housekeeping.Run(_rig.History, _rig.Pending, new YolimeSettings());

        var row = Assert.Single(_rig.History.Recent(10));
        Assert.Equal(runId, row.RunId);
        Assert.Equal(RunOutcome.Interrupted, row.Outcome);
        Assert.Equal(Mode.Raw, row.Mode);
        Assert.Equal($"{runId:N}.wav", row.PendingAudioFile);
    }

    [Fact]
    public async Task Startup_PruneMarksAudioExpired()
    {
        await using (var rec = _rig.Pending.Begin(Guid.NewGuid())) await rec.FinaliseAsync();
        var wav = _rig.Pending.List().Single();
        var id = _rig.History.Insert(Row(RunOutcome.SttFailed, wav));
        File.SetCreationTimeUtc(_rig.Pending.PathOf(wav), _rig.Time.GetUtcNow().UtcDateTime.AddDays(-8));

        Housekeeping.Run(_rig.History, _rig.Pending, new YolimeSettings());

        var row = _rig.History.Get(id)!;
        Assert.True(row.AudioExpired);
        Assert.Null(_rig.History.LatestRetryable());
    }

    [Fact]
    public void Startup_RowWithMissingWav_MarkedExpired()
    {
        var id = _rig.History.Insert(Row(RunOutcome.SttFailed, "gone.wav"));
        Housekeeping.Run(_rig.History, _rig.Pending, new YolimeSettings());
        Assert.True(_rig.History.Get(id)!.AudioExpired);
        Assert.Null(_rig.History.LatestRetryable());
    }

    [Fact]
    public void Startup_DeletesLeftoverRefusedRows()
    {
        var id = _rig.History.Insert(Row(RunOutcome.InProgress, delivery: DeliveryKind.Refused));
        Housekeeping.Run(_rig.History, _rig.Pending, new YolimeSettings());
        Assert.Null(_rig.History.Get(id));
    }
}
