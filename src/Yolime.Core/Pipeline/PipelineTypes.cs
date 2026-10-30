using Yolime.Core.Capture;
using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Logging;
using Yolime.Core.Modes;
using Yolime.Core.Stt;

namespace Yolime.Core.Pipeline;

/// <summary>Facts known only when capture ends: the mode after Shift-cycling and Shift-at-release.</summary>
public sealed record RunIntent(Mode Mode, bool PreviewRequested);

/// <param name="InitialMode">The mode at key-down (the app's starting mode), used until <paramref name="Intent"/> resolves.</param>
/// <param name="Intent">Completed by the App at key-up. The pipeline awaits it once the audio ends.</param>
/// <param name="RunId">Lets the caller tie the run to what it captured at key-down (the target window).</param>
/// <param name="Context">
/// Reply/Prompt context, read while the key is held. A separate task so neither STT nor the commit ever waits for
/// it: the pipeline awaits it just before the rewrite. Null when nothing will be read.
/// </param>
/// <param name="Profile">The target app's style when the hold began; sets tone and section names.</param>
public sealed record RunRequest(
    IAudioSource Audio,
    Mode InitialMode,
    string? ProcessName,
    Task<RunIntent> Intent,
    IProgress<PipelineEvent>? Progress = null,
    Guid? RunId = null,
    Task<CapturedContext?>? Context = null,
    Profile Profile = Profile.Neutral,
    Func<MicTiming>? Mic = null);

/// <param name="FirstAudioMs">Key-down to the mic's first buffer; null if it never delivered.</param>
/// <param name="Start">Whether the mic was already open (kept ready) at key-down.</param>
public readonly record struct MicTiming(long? FirstAudioMs, MicKind Kind, MicStart Start = MicStart.Cold);

public enum ContextSource { Selection, Clipboard }

/// <summary>What happened to the context, for the notice. Skipped: it held a secret, so it was never sent.</summary>
public enum ContextNote { None, Skipped }

/// <summary>Text the reply or prompt is about. Content: it goes to the model and history, never to logs.</summary>
/// <param name="AgeMs">How old the clipboard copy was when read; 0 for a selection.</param>
public sealed record CapturedContext(string Text, ContextSource Source, long AgeMs);

/// <summary>Progress for the overlay. Transcript and delta text go to the overlay only, never to logs.</summary>
public abstract record PipelineEvent;

public sealed record StageStarted(Stage Stage) : PipelineEvent;

public sealed record StageFinished(Stage Stage, StageStatus Status) : PipelineEvent;

public sealed record CaptureLevel(float Rms, long ElapsedMs) : PipelineEvent;

public sealed record TranscriptReady(string Text) : PipelineEvent;

public sealed record RewriteDelta(string Text) : PipelineEvent;

/// <summary>What is running now, so a rejected hold can say what is in progress and how to cancel it.</summary>
public sealed record CurrentRun(Guid RunId, Mode Mode, Stage Stage, DateTimeOffset StartedUtc);

public sealed record RunResult
{
    public required Guid RunId { get; init; }
    public required RunOutcome Outcome { get; init; }
    public required Mode Mode { get; init; }
    public string? RawText { get; init; }
    public string? Output { get; init; }
    public DeliveryKind Delivery { get; init; }
    public long? HistoryId { get; init; }
    public bool HistoryWritten { get; init; }
    public SttError? SttError { get; init; }
    public CaptureError? CaptureError { get; init; }
    public RewriteFailure? RewriteError { get; init; }
    public ContextNote ContextNote { get; init; }

    /// <summary>The model's rewrite was used (not a fallback, not local cleanup): "Use raw" is worth offering.</summary>
    public bool Rewritten { get; init; }

    /// <summary>Where the context sent to the model came from, if any. With <see cref="ContextChars"/>: a count, never content.</summary>
    public ContextSource? ContextSource { get; init; }
    public int ContextChars { get; init; }

    /// <summary>Set only when <see cref="Outcome"/> is <see cref="RunOutcome.Busy"/>.</summary>
    public CurrentRun? BusyWith { get; init; }

    /// <summary>Null only for <see cref="RunOutcome.Busy"/>, which isn't a run.</summary>
    public RunTiming? Timing { get; init; }

    /// <summary>History was paused for this run, so nothing was kept: no row, and its recording goes.</summary>
    public bool HistoryPaused { get; init; }
}

public static class SttTimeout
{
    /// <summary>15 s plus 0.3 s per second of audio: 16 s for a 3 s clip, 105 s for a 5-minute latched one.</summary>
    public static TimeSpan For(long audioBytes) =>
        TimeSpan.FromSeconds(15) + TimeSpan.FromMilliseconds(audioBytes / AudioFormat.BytesPerMs * 0.3);
}
