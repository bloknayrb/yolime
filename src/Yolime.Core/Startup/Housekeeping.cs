using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Settings;

namespace Yolime.Core.Startup;

/// <summary>Startup recovery and retention: makes crashed runs retryable and enforces the retention windows.</summary>
public static class Housekeeping
{
    public static readonly TimeSpan PendingAudioRetention = TimeSpan.FromDays(7);

    public static void Run(IHistoryStore history, PendingAudioStore pending, YolimeSettings settings)
    {
        // A paused run keeps nothing, even across a crash: its recording is deleted, never adopted below.
        foreach (var name in pending.List().Where(n => n.EndsWith(PendingAudioStore.PausedSuffix, StringComparison.Ordinal)))
            pending.Delete(name);
        try
        {
            history.MarkInterrupted();
            history.DeleteRefused();
            history.MarkAudioExpired(pending.PruneOlderThan(PendingAudioRetention));
            // Rows whose audio is gone (deleted by hand, or a delete whose row update failed) can't be retried.
            var onDisk = pending.List().ToHashSet(StringComparer.Ordinal);
            history.MarkAudioExpired(history.ReferencedAudioFiles().Where(name => !onDisk.Contains(name)).ToList());
            history.PruneOlderThan(TimeSpan.FromDays(settings.HistoryRetentionDays));

            // A crash before the row was written leaves audio with no row: give it one so it shows up for Retry.
            var referenced = history.ReferencedAudioFiles();
            foreach (var name in pending.List().Where(n => !referenced.Contains(n)))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out var runId)) continue;
                history.Insert(new HistoryRow
                {
                    RunId = runId,
                    StartedUtc = new DateTimeOffset(File.GetCreationTimeUtc(pending.PathOf(name)), TimeSpan.Zero),
                    Mode = Mode.Raw,
                    Outcome = RunOutcome.Interrupted,
                    PendingAudioFile = name,
                });
            }
        }
        catch (HistoryUnavailableException)
        {
            // History is read-only or broken: still prune audio, so it can't grow without bound.
            pending.PruneOlderThan(PendingAudioRetention);
        }
    }
}
