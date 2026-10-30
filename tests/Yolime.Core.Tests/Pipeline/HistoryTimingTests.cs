using Yolime.Core.History;

namespace Yolime.Core.Tests.Pipeline;

/// <summary>How long a run spent writing history, in total and before its paste (latency leftovers, PR B).</summary>
public sealed class HistoryTimingTests : IDisposable
{
    private readonly PipelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>The real store, with each write taking a known time on the fake clock (different per call).</summary>
    private sealed class SlowHistory(HistoryStore inner, Microsoft.Extensions.Time.Testing.FakeTimeProvider time) : IHistoryStore
    {
        public long Spent;
        private int _writes;

        private T Slow<T>(Func<T> write)
        {
            var ms = 3 + 2 * _writes++; // 3, 5, 7…: a "last write only" sum would be wrong
            time.Advance(TimeSpan.FromMilliseconds(ms));
            Spent += ms;
            return write();
        }

        public bool IsReadOnly => inner.IsReadOnly;
        public long Insert(HistoryRow row) => Slow(() => inner.Insert(row));
        public void Update(long id, Func<HistoryRow, HistoryRow> change) => Slow(() => { inner.Update(id, change); return 0; });
        public void Delete(long id) => inner.Delete(id);
        public HistoryRow? Get(long id) => inner.Get(id);
        public IReadOnlyList<HistoryRow> Recent(int limit) => inner.Recent(limit);
        public IReadOnlyList<HistoryRow> Page(int limit, HistoryCursor? before, Mode? mode) => inner.Page(limit, before, mode);
        public IReadOnlyList<HistoryRow> All() => inner.All();
        public IReadOnlyList<long> DeleteRun(long id, bool keepReruns = false) => inner.DeleteRun(id, keepReruns);
        public int RerunCount(long id) => inner.RerunCount(id);
        public HistoryRow? LatestRetryable() => inner.LatestRetryable();
        public int MarkInterrupted() => inner.MarkInterrupted();
        public int DeleteRefused() => inner.DeleteRefused();
        public void MarkAudioExpired(IEnumerable<string> fileNames) => inner.MarkAudioExpired(fileNames);
        public IReadOnlySet<string> ReferencedAudioFiles() => inner.ReferencedAudioFiles();
        public int PruneOlderThan(TimeSpan age) => inner.PruneOlderThan(age);
        public void Clear() => inner.Clear();
        public void Checkpoint() => Slow(() => { inner.Checkpoint(); return 0; });
    }

    [Fact]
    public async Task ARun_RecordsItsHistoryWrites_InTotalAndBeforeThePaste()
    {
        var slow = new SlowHistory(_rig.History, _rig.Time);
        await _rig.Build(slow).RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        var t = _rig.Timings.Last();
        Assert.True(slow.Spent > 10, $"{slow.Spent}");
        Assert.Equal(slow.Spent, t.HistoryWriteMs);
        // Some writes come after the paste (the final outcome), so the part before it is less, and not nothing.
        Assert.InRange(t.HistoryBeforePasteMs ?? -1, 1, t.HistoryWriteMs - 1);
    }

    [Fact]
    public async Task WithHistoryPaused_NoTimeIsCounted()
    {
        _rig.Settings = _rig.Settings with { HistoryPaused = true };
        var slow = new SlowHistory(_rig.History, _rig.Time);
        await _rig.Build(slow).RunAsync(PipelineRig.Request(PipelineRig.Speech()), CancellationToken.None);
        Assert.Equal(0, slow.Spent);
        Assert.Equal(0, _rig.Timings.Last().HistoryWriteMs);
    }
}
