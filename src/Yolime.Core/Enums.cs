namespace Yolime.Core;

public enum Mode { Dictate, Prompt, Reply, Raw }

public enum Stage { Capture, Stt, Rewrite, Output }

public enum StageStatus { Ok, Skipped, Failed, TimedOut, Cancelled, FellBack }

public enum RunOutcome
{
    InProgress,
    Completed,
    CompletedWithFallback,
    Cancelled,
    Discarded,
    NoSpeech,
    Busy,
    CaptureFailed,
    SttFailed,
    RewriteFailed,
    OutputFailed,
    Interrupted,
}

public enum SttProviderKind { OpenAi, Groq, Custom }
