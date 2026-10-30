using Yolime.Core.Audio;

namespace Yolime.Core.Logging;

/// <summary>
/// One line of the timing log per run. Milestones are milliseconds after the audio ended (key-up, or a retry's
/// start), so PR7 can compare release-to-paste latency across batch and streaming STT. The mic fields are the exception:
/// they're measured from key-down.
/// </summary>
public sealed record RunTiming
{
    public required Guid RunId { get; init; }
    public required Mode Mode { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required RunOutcome Outcome { get; init; }
    public long CaptureMs { get; init; }
    public long AudioBytes { get; init; }

    /// <summary>Key-down to the mic's first buffer. Null for retries and re-runs (no mic), or a mic that never delivered.</summary>
    public long? MicFirstAudioMs { get; init; }

    /// <summary>The kind of mic the hold used. Null for retries and re-runs.</summary>
    public MicKind? MicKind { get; init; }

    /// <summary>Cold (opened at key-down), Warm (kept ready) or WarmReopened (kept ready, but stalled). Null without a mic.</summary>
    public Yolime.Core.Capture.MicStart? MicStart { get; init; }

    /// <summary>What was actually uploaded after silence trimming.</summary>
    public long SttUploadBytes { get; init; }

    /// <summary>How the audio was sent (batch, streamed, or streamed then re-sent). Null when the run had no STT call.</summary>
    public Yolime.Core.Stt.SttUpload? SttUpload { get; init; }

    /// <summary>Which Claude model did the rewrite, when there was one (Prompt's model is a setting). An enum: logs hold no strings.</summary>
    public RewriteModelKind? RewriteModel { get; init; }
    public SttProviderKind Provider { get; init; }
    public bool NewConnection { get; init; }
    public long? SttBodySentMs { get; init; }
    public long? SttFirstByteMs { get; init; }
    public long? SttDoneMs { get; init; }
    public long? RewriteFirstTokenMs { get; init; }
    public long? RewriteDoneMs { get; init; }
    public long? OutputDoneMs { get; init; }
    public IReadOnlyDictionary<Stage, StageStatus> Stages { get; init; } = new Dictionary<Stage, StageStatus>();
    public int TranscriptChars { get; init; }
    public int OutputChars { get; init; }
    public int OutputWords { get; init; }

    /// <summary>
    /// Time inside the run's history writes, summed: in all (<see cref="HistoryWriteMs"/>), and up to the start of the
    /// paste (<see cref="HistoryBeforePasteMs"/>, null when the run never got there or history was paused), which is the
    /// part that delays it.
    /// </summary>
    public long HistoryWriteMs { get; init; }
    public long? HistoryBeforePasteMs { get; init; }

    /// <summary>Where a paste's time went (PR7): only for deliveries through the normal paste path.</summary>
    public OutputBreakdown? Output { get; init; }
}

/// <summary>Milliseconds per step of a paste, in order. Numbers only, like the rest of the timing log.</summary>
public sealed record OutputBreakdown(
    long GateWaitMs,
    long ModifierWaitMs,
    long SnapshotMs,
    long FlushMs,
    long ClipboardSaveMs,
    long ClipboardSetMs,
    long PasteMs,
    long ClipboardOpenMs = 0,
    int ClipboardOpenRetries = 0);

public enum RewriteModelKind { Other, Haiku, Sonnet }

public static class RewriteModelKinds
{
    public static RewriteModelKind Of(string modelId) => modelId switch
    {
        Modes.ModelIds.Haiku => RewriteModelKind.Haiku,
        Modes.ModelIds.Sonnet => RewriteModelKind.Sonnet,
        _ => RewriteModelKind.Other,
    };
}
