using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Logging;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.History;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingLog _log = new();
    private readonly PendingAudioStore _pending;
    private readonly List<HistoryStore> _stores = [];

    public HistoryStoreTests() => _pending = new PendingAudioStore(_root.Paths, _log, _time);

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        SqliteConnection.ClearAllPools();
        _root.Dispose();
    }

    private HistoryStore Open()
    {
        var store = new HistoryStore(_root.Paths, _log, _time, _pending);
        _stores.Add(store);
        return store;
    }

    private HistoryRow Row(RunOutcome outcome = RunOutcome.InProgress, string? raw = null, string? pending = null, DateTimeOffset? started = null) => new()
    {
        RunId = Guid.NewGuid(),
        StartedUtc = started ?? _time.GetUtcNow(),
        Mode = Mode.Dictate,
        ProcessName = "slack",
        RawText = raw,
        Outcome = outcome,
        PendingAudioFile = pending,
    };

    private T Scalar<T>(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_root.Paths.HistoryDb};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    [Fact]
    public void NewDb_CurrentUserVersion_WalOn_SecureDeleteOn()
    {
        var store = Open();
        Assert.Equal((long)HistorySchema.CurrentVersion, Scalar<long>("PRAGMA user_version"));
        Assert.Equal("wal", Scalar<string>("PRAGMA journal_mode"));
        Assert.True(store.SecureDeleteEnabled);
    }

    [Fact]
    public void InsertGet_RoundTrips_EmojiCjk20kChars()
    {
        var store = Open();
        var raw = "Zoë 🍋 東京で会いましょう " + new string('x', 20_000);
        var row = Row(raw: raw) with
        {
            Context = "ctx 🍋",
            Output = "out",
            SttError = SttError.Network,
            Stages = new Dictionary<Stage, StageStatus> { [Stage.Stt] = StageStatus.Failed },
            StageMs = new Dictionary<Stage, long> { [Stage.Stt] = 812 },
            TotalMs = 900,
        };
        var id = store.Insert(row);

        var back = store.Get(id)!;
        Assert.Equal(raw, back.RawText);
        Assert.Equal(row.RunId, back.RunId);
        Assert.Equal(row.StartedUtc.ToUnixTimeMilliseconds(), back.StartedUtc.ToUnixTimeMilliseconds());
        Assert.Equal(SttError.Network, back.SttError);
        Assert.Equal(StageStatus.Failed, back.Stages[Stage.Stt]);
        Assert.Equal(812, back.StageMs[Stage.Stt]);
        Assert.Equal("ctx 🍋", back.Context);
    }

    [Fact]
    public void Update_AppliesChange()
    {
        var store = Open();
        var id = store.Insert(Row());
        store.Update(id, r => r with { Output = "done", Outcome = RunOutcome.Completed, Delivery = DeliveryKind.Pasted });
        var back = store.Get(id)!;
        Assert.Equal("done", back.Output);
        Assert.Equal(RunOutcome.Completed, back.Outcome);
        Assert.Equal(DeliveryKind.Pasted, back.Delivery);
    }

    [Fact]
    public void Update_MissingId_ThrowsHistoryUnavailable() =>
        Assert.Throws<HistoryUnavailableException>(() => Open().Update(999, r => r));

    [Fact]
    public void Delete_RemovesRow()
    {
        var store = Open();
        var id = store.Insert(Row());
        store.Delete(id);
        Assert.Null(store.Get(id));
    }

    [Fact]
    public void LatestRetryable_SkipsExpiredAndCompleted()
    {
        var store = Open();
        var retryable = store.Insert(Row(RunOutcome.SttFailed, pending: "a.wav", started: _time.GetUtcNow().AddMinutes(-3)));
        store.Insert(Row(RunOutcome.Completed, started: _time.GetUtcNow().AddMinutes(-2)));
        var expired = store.Insert(Row(RunOutcome.NoSpeech, pending: "b.wav", started: _time.GetUtcNow().AddMinutes(-1)));
        store.Insert(Row(RunOutcome.Interrupted, raw: "no audio left"));
        store.MarkAudioExpired(["b.wav"]);

        Assert.Equal(retryable, store.LatestRetryable()!.Id);
        Assert.True(store.Get(expired)!.AudioExpired);
        Assert.Null(store.Get(expired)!.PendingAudioFile);
    }

    [Fact]
    public void MarkInterrupted_ConvertsInProgressOnly()
    {
        var store = Open();
        var running = store.Insert(Row(RunOutcome.InProgress, pending: "c.wav"));
        var done = store.Insert(Row(RunOutcome.Completed));

        Assert.Equal(1, store.MarkInterrupted());
        Assert.Equal(RunOutcome.Interrupted, store.Get(running)!.Outcome);
        Assert.Equal(RunOutcome.Completed, store.Get(done)!.Outcome);
        Assert.Equal(running, store.LatestRetryable()!.Id);
    }

    [Fact]
    public void ReferencedAudioFiles_ListsPendingNames()
    {
        var store = Open();
        store.Insert(Row(pending: "x.wav"));
        store.Insert(Row());
        Assert.Equal(["x.wav"], store.ReferencedAudioFiles());
    }

    [Fact]
    public async Task Prune_RemovesOnlyOlderThanRetention_AndTheirWavs()
    {
        var store = Open();
        await using (var rec = _pending.Begin(Guid.NewGuid())) await rec.FinaliseAsync();
        var wav = _pending.List().Single();
        var old = store.Insert(Row(RunOutcome.SttFailed, pending: wav, started: _time.GetUtcNow().AddDays(-31)));
        var recent = store.Insert(Row(started: _time.GetUtcNow().AddDays(-29)));

        Assert.Equal(1, store.PruneOlderThan(TimeSpan.FromDays(30)));
        Assert.Null(store.Get(old));
        Assert.NotNull(store.Get(recent));
        Assert.Empty(_pending.List());
    }

    [Fact]
    public async Task Clear_DeletesAllPendingWavs()
    {
        var store = Open();
        await using (var rec = _pending.Begin(Guid.NewGuid())) await rec.FinaliseAsync();
        await using (var rec = _pending.Begin(Guid.NewGuid())) await rec.FinaliseAsync();
        store.Insert(Row(pending: _pending.List()[0]));

        store.Clear();

        Assert.Empty(store.Recent(10));
        Assert.Empty(_pending.List());
    }

    [Fact]
    public void Clear_SentinelNotRecoverableFromDbWalOrShm()
    {
        var store = Open();
        for (var i = 0; i < 20; i++) store.Insert(Row(raw: "SENTINEL_HIST " + i) with { Output = "SENTINEL_HIST out" });
        store.Clear();
        store.Dispose();
        SqliteConnection.ClearAllPools();

        var needle = Encoding.UTF8.GetBytes("SENTINEL_HIST");
        foreach (var path in new[] { _root.Paths.HistoryDb, _root.Paths.HistoryDb + "-wal", _root.Paths.HistoryDb + "-shm" })
        {
            if (!File.Exists(path)) continue;
            Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(needle) < 0, $"sentinel recoverable from {Path.GetFileName(path)}");
        }
    }

    [Fact]
    public void NewerUserVersion_IsReadOnly_InsertThrowsHistoryUnavailable()
    {
        Open().Dispose();
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection($"Data Source={_root.Paths.HistoryDb};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 9";
            cmd.ExecuteNonQuery();
        }

        var store = Open();
        Assert.True(store.IsReadOnly);
        Assert.Throws<HistoryUnavailableException>(() => store.Insert(Row()));
        Assert.Contains(LogEvent.HistoryNewerSchema, _log.Events);
    }

    [Fact]
    public void UnreadableRow_ThrowsHistoryUnavailable_NotParseError()
    {
        var store = Open();
        var id = store.Insert(Row());
        using (var c = new SqliteConnection($"Data Source={_root.Paths.HistoryDb};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            // What a downgrade would meet: an enum value written by a newer version.
            cmd.CommandText = $"UPDATE runs SET outcome = 'FromTheFuture' WHERE id = {id}";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<HistoryUnavailableException>(() => store.Recent(10));
    }

    [Fact]
    public void Schema_HasNoTitleWindowOrAudioBlobColumns()
    {
        Open();
        using var c = new SqliteConnection($"Data Source={_root.Paths.HistoryDb};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, type FROM pragma_table_info('runs')";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.GetString(0);
            Assert.DoesNotContain("title", name);
            Assert.DoesNotContain("window", name);
            Assert.NotEqual("BLOB", r.GetString(1), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void HistorySchema_MigratesThroughFakeStep()
    {
        Open().Dispose();
        SqliteConnection.ClearAllPools();
        var next = HistorySchema.CurrentVersion;
        var steps = new Dictionary<int, string>(HistorySchema.Steps) { [next] = "ALTER TABLE runs ADD COLUMN extra TEXT" };
        using var store = new HistoryStore(_root.Paths, _log, _time, _pending, currentVersion: next + 1, steps);
        Assert.Equal((long)next + 1, Scalar<long>("PRAGMA user_version"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name = 'extra'"));
        Assert.Contains(LogEvent.HistoryMigrated, _log.Events);
    }
}
