using Microsoft.Data.Sqlite;

namespace Yolime.Core.History;

/// <summary>Schema migrations keyed by the version they upgrade from, tracked in <c>PRAGMA user_version</c>.</summary>
public static class HistorySchema
{
    public const int CurrentVersion = 3;

    public static readonly IReadOnlyDictionary<int, string> Steps = new Dictionary<int, string>
    {
        [0] = """
            CREATE TABLE runs (
                id INTEGER PRIMARY KEY,
                run_id TEXT NOT NULL UNIQUE,
                started_ms INTEGER NOT NULL,
                mode TEXT NOT NULL,
                process_name TEXT,
                raw_text TEXT,
                context TEXT,
                output TEXT,
                outcome TEXT NOT NULL,
                delivery TEXT NOT NULL,
                stt_error TEXT,
                rewrite_error TEXT,
                capture_error TEXT,
                fallback_used INTEGER NOT NULL DEFAULT 0,
                pending_audio_file TEXT,
                audio_expired INTEGER NOT NULL DEFAULT 0,
                stages TEXT NOT NULL DEFAULT '{}',
                stage_ms TEXT NOT NULL DEFAULT '{}',
                total_ms INTEGER
            );
            CREATE INDEX runs_started ON runs(started_ms);
            """,
        [1] = "ALTER TABLE runs ADD COLUMN context_source TEXT;",
        // PR6: a re-run is its own row that points at the run it was made from.
        [2] = "ALTER TABLE runs ADD COLUMN rerun_of INTEGER;",
    };

    /// <returns>The version the database was at before migrating.</returns>
    internal static int Migrate(SqliteConnection connection, int currentVersion, IReadOnlyDictionary<int, string> steps)
    {
        var version = UserVersion(connection);
        if (version >= currentVersion) return version;
        using var tx = connection.BeginTransaction();
        for (var v = version; v < currentVersion; v++)
        {
            if (!steps.TryGetValue(v, out var sql)) continue;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        using (var set = connection.CreateCommand())
        {
            set.Transaction = tx;
            set.CommandText = $"PRAGMA user_version = {currentVersion}";
            set.ExecuteNonQuery();
        }
        tx.Commit();
        return version;
    }

    internal static int UserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
