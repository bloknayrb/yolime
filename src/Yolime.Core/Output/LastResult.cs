using Yolime.Core.History;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Output;

/// <summary>
/// The text "Paste last result" pastes: the last output delivered in this session, held in memory only.
/// </summary>
/// <remarks>
/// It is kept for <see cref="Keep"/> at most, never recorded while history is paused (pausing history means yolime
/// keeps nothing), and cleared by a refused or discarded run so text the user turned away can't come back with a
/// chord. Runs that fail before output (no speech, a failed transcription) leave the previous result alone.
/// </remarks>
public sealed class LastResult(TimeProvider time, Func<bool> historyPaused)
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);

    private readonly Lock _gate = new();
    private (string Text, Mode Mode, DateTimeOffset At, long? HistoryId)? _last;
    private long _generation;

    /// <summary>Bumped by every <see cref="Clear"/>. A run takes it when it starts and hands it back to <see cref="Record"/>.</summary>
    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    /// <summary>Records with the current generation (for callers with no run in flight, such as tests).</summary>
    public void Record(RunResult result) => Record(result, Generation);

    /// <param name="startedGeneration">
    /// <see cref="Generation"/> when the run started. A Clear (or a pause toggle) since then means this result belongs to
    /// what was cleared: the pipeline lets Clear in before the run's result reaches here.
    /// </param>
    public void Record(RunResult result, long startedGeneration)
    {
        lock (_gate)
        {
            if (startedGeneration != _generation) return;
            if (result.Delivery is DeliveryKind.Refused or DeliveryKind.Discarded)
            {
                _last = null;
                return;
            }
            if (historyPaused()) return;
            if (result.Outcome is not (RunOutcome.Completed or RunOutcome.CompletedWithFallback)) return;
            if (result.Delivery is not (DeliveryKind.Pasted or DeliveryKind.Copied or DeliveryKind.PendingPaste)) return;
            if (string.IsNullOrEmpty(result.Output)) return;
            _last = (result.Output, result.Mode, time.GetUtcNow(), result.HistoryId);
        }
    }

    /// <summary>A run deleted in History takes its text out of paste-last too.</summary>
    public void ForgetRuns(IEnumerable<long> historyIds)
    {
        lock (_gate)
        {
            if (_last is not { HistoryId: { } id } || !historyIds.Contains(id)) return;
            _last = null;
            // Like Clear: a run that was already finishing when its row was deleted mustn't record its text back.
            _generation++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _last = null;
            _generation++;
        }
    }

    public bool TryGet(out string text, out Mode mode)
    {
        lock (_gate)
        {
            if (_last is { } last && !historyPaused() && time.GetUtcNow() - last.At <= Keep)
            {
                (text, mode) = (last.Text, last.Mode);
                return true;
            }
            if (_last is not null && time.GetUtcNow() - _last.Value.At > Keep) _last = null;
            (text, mode) = ("", default);
            return false;
        }
    }
}
