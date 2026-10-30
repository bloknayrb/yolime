using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Yolime.Core.Audio;
using Yolime.Core.Logging;
using Yolime.Core.Stt;

namespace Yolime.Core.History;

/// <summary>
/// SQLite history at <see cref="AppPaths.HistoryDb"/>. Deleted rows are zeroed (<c>secure_delete</c>) and Clear also
/// checkpoints and vacuums, so cleared text isn't recoverable from the database or its WAL. Every failure surfaces
/// as <see cref="HistoryUnavailableException"/>, which callers treat as "carry on without history".
/// </summary>
public sealed class HistoryStore : IHistoryStore, IDisposable
{
    private static readonly RunOutcome[] RetryableOutcomes = [RunOutcome.SttFailed, RunOutcome.CaptureFailed, RunOutcome.NoSpeech, RunOutcome.Interrupted, RunOutcome.Cancelled];

    private readonly SqliteConnection _db;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly PendingAudioStore _pending;
    private readonly Lock _gate = new();
    private bool _disposed;

    public HistoryStore(AppPaths paths, ILog log, TimeProvider time, PendingAudioStore pending)
        : this(paths, log, time, pending, HistorySchema.CurrentVersion, HistorySchema.Steps)
    {
    }

    internal HistoryStore(AppPaths paths, ILog log, TimeProvider time, PendingAudioStore pending, int currentVersion, IReadOnlyDictionary<int, string> steps)
    {
        _log = log;
        _time = time;
        _pending = pending;
        Directory.CreateDirectory(paths.LocalDir);
        var cs = new SqliteConnectionStringBuilder { DataSource = paths.HistoryDb, Pooling = false }.ToString();
        _db = new SqliteConnection(cs);
        _db.Open();
        Exec("PRAGMA secure_delete = ON");

        var found = HistorySchema.UserVersion(_db);
        if (found > currentVersion)
        {
            IsReadOnly = true;
            log.Write(LogEvent.HistoryNewerSchema, LogField.Count("found", found), LogField.Count("known", currentVersion));
            return;
        }
        // journal_mode can't change inside a transaction, so set it before migrating.
        Exec("PRAGMA journal_mode = WAL");
        var from = HistorySchema.Migrate(_db, currentVersion, steps);
        if (from != currentVersion && from != 0)
            log.Write(LogEvent.HistoryMigrated, LogField.Count("from", from), LogField.Count("to", currentVersion));
    }

    public bool IsReadOnly { get; }

    internal bool SecureDeleteEnabled => Convert.ToInt64(Scalar("PRAGMA secure_delete")) == 1;

    public long Insert(HistoryRow row) => Write(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs (run_id, started_ms, mode, process_name, raw_text, context, context_source, output, outcome, delivery,
                stt_error, rewrite_error, capture_error, fallback_used, pending_audio_file, audio_expired, stages, stage_ms, total_ms, rerun_of)
            VALUES ($run_id, $started_ms, $mode, $process_name, $raw_text, $context, $context_source, $output, $outcome, $delivery,
                $stt_error, $rewrite_error, $capture_error, $fallback_used, $pending_audio_file, $audio_expired, $stages, $stage_ms, $total_ms, $rerun_of);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, row);
        return Convert.ToInt64(cmd.ExecuteScalar());
    });

    public void Update(long id, Func<HistoryRow, HistoryRow> change) => Write(() =>
    {
        var current = ReadOne("WHERE id = $id", ("$id", id)) ?? throw new HistoryUnavailableException();
        var next = change(current) with { Id = id };
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET run_id = $run_id, started_ms = $started_ms, mode = $mode, process_name = $process_name,
                raw_text = $raw_text, context = $context, context_source = $context_source, output = $output, outcome = $outcome, delivery = $delivery,
                stt_error = $stt_error, rewrite_error = $rewrite_error, capture_error = $capture_error,
                fallback_used = $fallback_used, pending_audio_file = $pending_audio_file, audio_expired = $audio_expired,
                stages = $stages, stage_ms = $stage_ms, total_ms = $total_ms, rerun_of = $rerun_of
            WHERE id = $id
            """;
        Bind(cmd, next);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return 0;
    });

    public void Delete(long id) => Write(() => Exec("DELETE FROM runs WHERE id = $id", ("$id", id)));

    public HistoryRow? Get(long id) => Read(() => ReadOne("WHERE id = $id", ("$id", id)));

    public IReadOnlyList<HistoryRow> Recent(int limit) =>
        Read(() => ReadMany("ORDER BY started_ms DESC, id DESC LIMIT $limit", ("$limit", limit)));

    public IReadOnlyList<HistoryRow> Page(int limit, HistoryCursor? before, Mode? mode) => Read(() =>
    {
        var where = $"WHERE outcome <> '{RunOutcome.InProgress}'";
        var args = new List<(string, object)> { ("$limit", limit) };
        if (before is { } c)
        {
            // Keyset on the list's own order: ids don't follow time (orphan adoption gives old runs new ids).
            where += " AND (started_ms < $s OR (started_ms = $s AND id < $id))";
            args.Add(("$s", c.StartedMs));
            args.Add(("$id", c.Id));
        }
        if (mode is { } m)
        {
            where += " AND mode = $mode";
            args.Add(("$mode", m.ToString()));
        }
        return (IReadOnlyList<HistoryRow>)ReadMany($"{where} ORDER BY started_ms DESC, id DESC LIMIT $limit", skipUnreadable: true, [.. args]);
    });

    public IReadOnlyList<HistoryRow> All() => Read(() =>
        (IReadOnlyList<HistoryRow>)ReadMany($"WHERE outcome <> '{RunOutcome.InProgress}' ORDER BY started_ms DESC, id DESC", skipUnreadable: true));

    public int RerunCount(long id) => Read(() => Convert.ToInt32(ScalarWith("SELECT COUNT(*) FROM runs WHERE rerun_of = $id", ("$id", id))));

    public IReadOnlyList<long> DeleteRun(long id, bool keepReruns = false) => Write(() =>
    {
        // Kept re-runs stand on their own from now on, so deleting the original can't take them later.
        if (keepReruns) Exec("UPDATE runs SET rerun_of = NULL WHERE rerun_of = $id", ("$id", id));
        var ids = new List<long>();
        using (var which = _db.CreateCommand())
        {
            which.CommandText = "SELECT id FROM runs WHERE id = $id OR rerun_of = $id";
            which.Parameters.AddWithValue("$id", id);
            using var r = which.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        using (var files = _db.CreateCommand())
        {
            files.CommandText = "SELECT pending_audio_file FROM runs WHERE (id = $id OR rerun_of = $id) AND pending_audio_file IS NOT NULL";
            files.Parameters.AddWithValue("$id", id);
            using var r = files.ExecuteReader();
            while (r.Read()) _pending.Delete(r.GetString(0));
        }
        Exec("DELETE FROM runs WHERE id = $id OR rerun_of = $id", ("$id", id));
        // secure_delete zeroed the pages; the checkpoint moves them out of the WAL too.
        CheckpointTruncate();
        return (IReadOnlyList<long>)ids;
    });

    public HistoryRow? LatestRetryable() => Read(() => ReadOne(
        $"WHERE (outcome IN ({string.Join(",", RetryableOutcomes.Select(o => $"'{o}'"))}) AND pending_audio_file IS NOT NULL AND audio_expired = 0) "
        // A Prompt or Reply whose rewrite failed went to the clipboard as raw text: its transcript can be rewritten again.
        + $"OR (outcome = '{RunOutcome.CompletedWithFallback}' AND rewrite_error IS NOT NULL AND raw_text IS NOT NULL AND mode IN ('{Mode.Prompt}', '{Mode.Reply}')) "
        + "ORDER BY started_ms DESC, id DESC LIMIT 1"));

    public int MarkInterrupted() => Write(() =>
        Exec($"UPDATE runs SET outcome = '{RunOutcome.Interrupted}' WHERE outcome = '{RunOutcome.InProgress}'"));

    public int DeleteRefused() => Write(() => Exec($"DELETE FROM runs WHERE delivery = '{DeliveryKind.Refused}'"));

    public void MarkAudioExpired(IEnumerable<string> fileNames) => Write(() =>
    {
        foreach (var name in fileNames)
            Exec("UPDATE runs SET audio_expired = 1, pending_audio_file = NULL WHERE pending_audio_file = $f", ("$f", name));
        return 0;
    });

    public IReadOnlySet<string> ReferencedAudioFiles() => Read(() =>
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT pending_audio_file FROM runs WHERE pending_audio_file IS NOT NULL";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) set.Add(r.GetString(0));
        return (IReadOnlySet<string>)set;
    });

    public int PruneOlderThan(TimeSpan age) => Write(() =>
    {
        var cutoff = (_time.GetUtcNow() - age).ToUnixTimeMilliseconds();
        using (var files = _db.CreateCommand())
        {
            files.CommandText = "SELECT pending_audio_file FROM runs WHERE started_ms < $c AND pending_audio_file IS NOT NULL";
            files.Parameters.AddWithValue("$c", cutoff);
            using var r = files.ExecuteReader();
            while (r.Read()) _pending.Delete(r.GetString(0));
        }
        var n = Exec("DELETE FROM runs WHERE started_ms < $c", ("$c", cutoff));
        if (n > 0)
        {
            _log.Write(LogEvent.HistoryPruned, LogField.Count("rows", n));
            // Like Delete: the pruned text mustn't wait in the WAL for an automatic checkpoint.
            CheckpointTruncate();
        }
        return n;
    });

    public void Clear() => Write(() =>
    {
        Exec("DELETE FROM runs");
        _pending.DeleteAll();
        // secure_delete zeroes the freed pages; checkpoint + vacuum make sure no copy survives in the WAL either.
        Exec("PRAGMA wal_checkpoint(TRUNCATE)");
        Exec("VACUUM");
        Exec("PRAGMA wal_checkpoint(TRUNCATE)");
        return 0;
    });

    /// <summary>After a refused delivery's row is deleted, flush the WAL so the text isn't left in it.</summary>
    public void Checkpoint() => Write(() =>
    {
        CheckpointTruncate();
        return 0;
    });

    /// <summary>
    /// A blocked checkpoint is reported as a result row (busy = 1), not an error, so it's read and logged: the deleted
    /// text then waits in the WAL until the next checkpoint succeeds.
    /// </summary>
    private bool CheckpointTruncate()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var r = cmd.ExecuteReader();
        var busy = r.Read() && r.GetInt64(0) != 0;
        if (busy) _log.Write(LogEvent.HistoryWriteFailed, LogField.Count("checkpointBusy", 1));
        return !busy;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _db.Dispose();
        }
    }

    private T Write<T>(Func<T> action)
    {
        if (IsReadOnly) throw new HistoryUnavailableException();
        return Read(action);
    }

    private T Read<T>(Func<T> action)
    {
        lock (_gate)
        {
            // A timer or a window finishing after shutdown gets the usual "unavailable", not a closed-connection error.
            if (_disposed) throw new HistoryUnavailableException();
            try
            {
                return action();
            }
            // Parse failures too: a row written by a newer version (an enum value this one doesn't know) must read
            // as "history unavailable", which callers handle, not as an exception type they don't.
            catch (Exception ex) when (ex is SqliteException or FormatException or ArgumentException or JsonException or InvalidCastException)
            {
                _log.Write(LogEvent.HistoryWriteFailed, LogField.Error(ex));
                throw new HistoryUnavailableException();
            }
        }
    }

    private int Exec(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    private object? ScalarWith(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    private object? Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private HistoryRow? ReadOne(string where, params (string Name, object Value)[] args) => ReadMany(where, skipUnreadable: false, args).FirstOrDefault();

    private List<HistoryRow> ReadMany(string tail, params (string Name, object Value)[] args) => ReadMany(tail, skipUnreadable: false, args);

    /// <param name="skipUnreadable">For the History window: one row a newer build wrote mustn't hide all the others.</param>
    private List<HistoryRow> ReadMany(string tail, bool skipUnreadable, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM runs {tail}";
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        var rows = new List<HistoryRow>();
        while (r.Read())
        {
            try
            {
                rows.Add(ReadRow(r));
            }
            catch (Exception ex) when (skipUnreadable && ex is FormatException or ArgumentException or JsonException or InvalidCastException)
            {
                // Skipped; the rest of the page still shows.
            }
        }
        return rows;
    }

    private static HistoryRow ReadRow(SqliteDataReader r) => new()
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                RunId = Guid.Parse(r.GetString(r.GetOrdinal("run_id"))),
                StartedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("started_ms"))),
                Mode = Enum.Parse<Mode>(r.GetString(r.GetOrdinal("mode"))),
                ProcessName = Text(r, "process_name"),
                RawText = Text(r, "raw_text"),
                Context = Text(r, "context"),
                ContextSource = Text(r, "context_source") is { } cs && Enum.TryParse<Pipeline.ContextSource>(cs, out var source) ? source : null,
                Output = Text(r, "output"),
                Outcome = Enum.Parse<RunOutcome>(r.GetString(r.GetOrdinal("outcome"))),
                Delivery = Enum.Parse<DeliveryKind>(r.GetString(r.GetOrdinal("delivery"))),
                SttError = Text(r, "stt_error") is { } s ? Enum.Parse<SttError>(s) : null,
                RewriteError = Text(r, "rewrite_error"),
                CaptureError = Text(r, "capture_error") is { } c ? Enum.Parse<CaptureError>(c) : null,
                FallbackUsed = r.GetInt64(r.GetOrdinal("fallback_used")) != 0,
                PendingAudioFile = Text(r, "pending_audio_file"),
                AudioExpired = r.GetInt64(r.GetOrdinal("audio_expired")) != 0,
                Stages = JsonSerializer.Deserialize(r.GetString(r.GetOrdinal("stages")), HistoryJsonContext.Default.DictionaryStageStageStatus) ?? [],
                StageMs = JsonSerializer.Deserialize(r.GetString(r.GetOrdinal("stage_ms")), HistoryJsonContext.Default.DictionaryStageInt64) ?? [],
                TotalMs = r.IsDBNull(r.GetOrdinal("total_ms")) ? null : r.GetInt64(r.GetOrdinal("total_ms")),
                RerunOf = r.IsDBNull(r.GetOrdinal("rerun_of")) ? null : r.GetInt64(r.GetOrdinal("rerun_of")),
            };

    private static string? Text(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static void Bind(SqliteCommand cmd, HistoryRow row)
    {
        object Db(object? v) => v ?? DBNull.Value;
        cmd.Parameters.AddWithValue("$run_id", row.RunId.ToString("N"));
        cmd.Parameters.AddWithValue("$started_ms", row.StartedUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$mode", row.Mode.ToString());
        cmd.Parameters.AddWithValue("$process_name", Db(row.ProcessName));
        cmd.Parameters.AddWithValue("$raw_text", Db(row.RawText));
        cmd.Parameters.AddWithValue("$context", Db(row.Context));
        cmd.Parameters.AddWithValue("$context_source", Db(row.ContextSource?.ToString()));
        cmd.Parameters.AddWithValue("$output", Db(row.Output));
        cmd.Parameters.AddWithValue("$outcome", row.Outcome.ToString());
        cmd.Parameters.AddWithValue("$delivery", row.Delivery.ToString());
        cmd.Parameters.AddWithValue("$stt_error", Db(row.SttError?.ToString()));
        cmd.Parameters.AddWithValue("$rewrite_error", Db(row.RewriteError));
        cmd.Parameters.AddWithValue("$capture_error", Db(row.CaptureError?.ToString()));
        cmd.Parameters.AddWithValue("$fallback_used", row.FallbackUsed ? 1 : 0);
        cmd.Parameters.AddWithValue("$pending_audio_file", Db(row.PendingAudioFile));
        cmd.Parameters.AddWithValue("$audio_expired", row.AudioExpired ? 1 : 0);
        cmd.Parameters.AddWithValue("$stages", JsonSerializer.Serialize(new Dictionary<Stage, StageStatus>(row.Stages), HistoryJsonContext.Default.DictionaryStageStageStatus));
        cmd.Parameters.AddWithValue("$stage_ms", JsonSerializer.Serialize(new Dictionary<Stage, long>(row.StageMs), HistoryJsonContext.Default.DictionaryStageInt64));
        cmd.Parameters.AddWithValue("$total_ms", Db(row.TotalMs));
        cmd.Parameters.AddWithValue("$rerun_of", Db(row.RerunOf));
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dictionary<Stage, StageStatus>))]
[JsonSerializable(typeof(Dictionary<Stage, long>))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
