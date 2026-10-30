using Yolime.Core.Audio;
using Yolime.Core.Stt;

namespace Yolime.Core.History;

public enum DeliveryKind { None, Pasted, Copied, PendingPaste, Refused, Discarded }

/// <summary>
/// One run in history. It holds the process name only, never a window title, and never the audio itself, only the
/// pending file's name while that file still exists.
/// </summary>
public sealed record HistoryRow
{
    public long Id { get; init; }
    public required Guid RunId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required Mode Mode { get; init; }
    public string? ProcessName { get; init; }
    public string? RawText { get; init; }
    public string? Context { get; init; }

    /// <summary>Where <see cref="Context"/> came from (schema v2).</summary>
    public Pipeline.ContextSource? ContextSource { get; init; }
    public string? Output { get; init; }
    public RunOutcome Outcome { get; init; } = RunOutcome.InProgress;
    public DeliveryKind Delivery { get; init; } = DeliveryKind.None;
    public SttError? SttError { get; init; }

    /// <summary>A <c>RewriteFailure</c> name, stored as text so PR3 needs no migration.</summary>
    public string? RewriteError { get; init; }

    public CaptureError? CaptureError { get; init; }
    public bool FallbackUsed { get; init; }
    public string? PendingAudioFile { get; init; }
    public bool AudioExpired { get; init; }
    public IReadOnlyDictionary<Stage, StageStatus> Stages { get; init; } = new Dictionary<Stage, StageStatus>();
    public IReadOnlyDictionary<Stage, long> StageMs { get; init; } = new Dictionary<Stage, long>();
    public long? TotalMs { get; init; }

    /// <summary>The run this one re-ran in another mode (schema v3); deleted with it.</summary>
    public long? RerunOf { get; init; }
}

/// <summary>Where a page of history starts: just past this row, in list order (newest first).</summary>
public readonly record struct HistoryCursor(long StartedMs, long Id)
{
    public static HistoryCursor After(HistoryRow row) => new(row.StartedUtc.ToUnixTimeMilliseconds(), row.Id);
}

/// <summary>History can't be written: a newer database, a locked file, a full disk. Never blocks delivery.</summary>
public sealed class HistoryUnavailableException() : Exception("history is unavailable");

public interface IHistoryStore
{
    bool IsReadOnly { get; }
    long Insert(HistoryRow row);
    void Update(long id, Func<HistoryRow, HistoryRow> change);
    void Delete(long id);
    HistoryRow? Get(long id);
    IReadOnlyList<HistoryRow> Recent(int limit);

    /// <summary>
    /// A page for the History window, newest first, after <paramref name="before"/>. Runs in progress and rows this
    /// build can't read are left out.
    /// </summary>
    IReadOnlyList<HistoryRow> Page(int limit, HistoryCursor? before, Mode? mode);

    /// <summary>Every finished run (retention bounds it), for searching in memory. Unreadable rows are skipped.</summary>
    IReadOnlyList<HistoryRow> All();

    /// <summary>
    /// Deletes a run, its re-runs (unless <paramref name="keepReruns"/>, which detaches them) and their recordings, then
    /// flushes the WAL. Returns the ids deleted.
    /// </summary>
    IReadOnlyList<long> DeleteRun(long id, bool keepReruns = false);

    int RerunCount(long id);

    /// <summary>The newest failed or interrupted run whose audio still exists.</summary>
    HistoryRow? LatestRetryable();

    /// <summary>At startup: runs left <see cref="RunOutcome.InProgress"/> by a crash become Interrupted.</summary>
    int MarkInterrupted();

    /// <summary>Rows whose delivery was refused (a password field) whose delete failed earlier.</summary>
    int DeleteRefused();

    void MarkAudioExpired(IEnumerable<string> fileNames);
    IReadOnlySet<string> ReferencedAudioFiles();
    int PruneOlderThan(TimeSpan age);
    void Clear();

    /// <summary>Flushes the WAL, so a just-deleted row's text isn't left in it.</summary>
    void Checkpoint();
}
