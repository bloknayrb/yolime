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

    /// <summary>
    /// A spoken command with nothing after it ("Write a prompt."), or a "Write a reply" with nothing to reply to.
    /// Nothing was sent to Claude; the transcript stays in history but the run isn't retryable (it would match again).
    /// </summary>
    CommandOnly,
}

public enum SttProviderKind { OpenAi, Groq, Custom }
