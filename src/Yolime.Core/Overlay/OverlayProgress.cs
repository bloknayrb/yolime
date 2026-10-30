using Yolime.Core.Pipeline;

namespace Yolime.Core.Overlay;

/// <summary>
/// The pipeline's progress, turned into overlay inputs as they happen (synchronously, so their order survives:
/// <see cref="Progress{T}"/> would post each one to the thread pool). Level updates are thinned to about 30 a second.
/// </summary>
public sealed class OverlayProgress(Action<OverlayInput> overlay, TimeProvider time) : IProgress<PipelineEvent>
{
    private const long LevelEveryMs = 33;
    private long _lastLevelMs;
    private bool _anyLevel;

    private long NowMs => (long)time.GetTimestamp() * 1000 / time.TimestampFrequency;

    public void Report(PipelineEvent value)
    {
        switch (value)
        {
            case CaptureLevel level:
                var now = NowMs;
                // A flag, not a long.MinValue start: "now - MinValue" overflows and dropped every level.
                if (_anyLevel && now - _lastLevelMs < LevelEveryMs) return;
                (_anyLevel, _lastLevelMs) = (true, now);
                overlay(new Level(level.Rms, now));
                break;
            case StageStarted started:
                overlay(new StageBegan(started.Stage, NowMs));
                break;
            case Pipeline.TranscriptReady transcript:
                overlay(new TranscriptShown(transcript.Text));
                break;
            case Pipeline.RewriteDelta delta:
                overlay(new DeltaArrived(delta.Text, NowMs));
                break;
        }
    }
}
