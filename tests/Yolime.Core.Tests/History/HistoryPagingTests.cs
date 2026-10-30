using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Audio;
using Yolime.Core.History;

namespace Yolime.Core.Tests.History;

/// <summary>PR6: what the History window reads and deletes.</summary>
public sealed class HistoryPagingTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingLog _log = new();
    private readonly PendingAudioStore _pending;
    private readonly HistoryStore _store;

    public HistoryPagingTests()
    {
        _pending = new PendingAudioStore(_root.Paths, _log, _time);
        _store = new HistoryStore(_root.Paths, _log, _time, _pending);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        _root.Dispose();
    }

    private long Add(int minutesAgo, Mode mode = Mode.Dictate, RunOutcome outcome = RunOutcome.Completed, string? output = "text", long? rerunOf = null, string? wav = null) =>
        _store.Insert(new HistoryRow
        {
            RunId = Guid.NewGuid(),
            StartedUtc = _time.GetUtcNow().AddMinutes(-minutesAgo),
            Mode = mode,
            ProcessName = "slack",
            RawText = "raw " + minutesAgo,
            Output = output,
            Outcome = outcome,
            RerunOf = rerunOf,
            PendingAudioFile = wav,
        });

    [Fact]
    public void Pages_FollowTheListOrder_EvenWhenIdsDont()
    {
        // An orphan WAV adopted at startup gets a new (high) id with an old time: paging by id would skip or repeat it.
        var newest = Add(1);
        var middle = Add(5);
        var adopted = Add(3);
        var oldest = Add(9);

        var first = _store.Page(2, before: null, mode: null);
        Assert.Equal([newest, adopted], first.Select(r => r.Id));
        var second = _store.Page(2, before: HistoryCursor.After(first[^1]), mode: null);
        Assert.Equal([middle, oldest], second.Select(r => r.Id));
        Assert.Empty(_store.Page(2, before: HistoryCursor.After(second[^1]), mode: null));
    }

    [Fact]
    public void Pages_LeaveOutRunsInProgress_AndFilterByMode()
    {
        Add(1, outcome: RunOutcome.InProgress);
        var reply = Add(2, Mode.Reply);
        Add(3, Mode.Dictate);
        Assert.Equal(2, _store.Page(10, null, null).Count);
        Assert.Equal([reply], _store.Page(10, null, Mode.Reply).Select(r => r.Id));
    }

    [Fact]
    public void ARowThisBuildCantRead_IsSkipped_NotFatal()
    {
        var good = Add(1);
        var bad = Add(2);
        using (var c = new SqliteConnection($"Data Source={_root.Paths.HistoryDb};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE runs SET outcome = 'FromTheFuture' WHERE id = {bad}";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal([good], _store.Page(10, null, null).Select(r => r.Id));
        Assert.Equal([good], _store.All().Select(r => r.Id));
    }

    [Fact]
    public void ReRuns_RecordTheirSource_AndAreDeletedWithIt()
    {
        var source = Add(5);
        var rerun = Add(5, Mode.Raw, rerunOf: source);
        Assert.Equal(source, _store.Get(rerun)!.RerunOf);
        Assert.Equal(1, _store.RerunCount(source));

        Assert.Equal([source, rerun], _store.DeleteRun(source).Order());
        Assert.Null(_store.Get(source));
        Assert.Null(_store.Get(rerun));
    }

    [Fact]
    public async Task DeleteRun_RemovesItsRecording()
    {
        await using (var rec = _pending.Begin(Guid.NewGuid())) await rec.FinaliseAsync();
        var wav = _pending.List().Single();
        var id = Add(1, outcome: RunOutcome.SttFailed, output: null, wav: wav);
        _store.DeleteRun(id);
        Assert.Empty(_pending.List());
    }

    [Fact]
    public void DeleteRun_LeavesNoTraceInTheDatabaseOrItsWal()
    {
        Add(2, output: "keep this");
        var id = Add(1, output: "SENTINEL_DELETE gone");
        _store.DeleteRun(id);
        _store.Dispose();
        SqliteConnection.ClearAllPools();

        var needle = Encoding.UTF8.GetBytes("SENTINEL_DELETE");
        foreach (var path in new[] { _root.Paths.HistoryDb, _root.Paths.HistoryDb + "-wal" })
        {
            if (!File.Exists(path)) continue;
            Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(needle) < 0, $"sentinel recoverable from {Path.GetFileName(path)}");
        }
    }

    [Fact]
    public void Prune_LeavesNoTraceInTheWal()
    {
        Add(60 * 24 * 40, output: "SENTINEL_PRUNE old");
        Add(1);
        _store.PruneOlderThan(TimeSpan.FromDays(30));
        _store.Dispose();
        SqliteConnection.ClearAllPools();

        var wal = _root.Paths.HistoryDb + "-wal";
        if (File.Exists(wal)) Assert.True(File.ReadAllBytes(wal).AsSpan().IndexOf(Encoding.UTF8.GetBytes("SENTINEL_PRUNE")) < 0);
    }

    [Fact]
    public void DeleteRun_CanKeepTheReRuns()
    {
        var source = Add(5);
        var rerun = Add(5, Mode.Prompt, rerunOf: source);
        Assert.Equal([source], _store.DeleteRun(source, keepReruns: true));
        var kept = _store.Get(rerun)!;
        Assert.Null(kept.RerunOf); // detached: it stands on its own now
    }

    [Fact]
    public void AfterDispose_TheStoreSaysUnavailable_NotAClosedConnectionError()
    {
        _store.Dispose();
        Assert.Throws<HistoryUnavailableException>(() => _store.Page(10, null, null));
        Assert.Throws<HistoryUnavailableException>(() => _store.PruneOlderThan(TimeSpan.FromDays(1)));
    }
}
