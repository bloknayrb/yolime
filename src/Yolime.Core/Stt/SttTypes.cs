namespace Yolime.Core.Stt;

/// <param name="TrimBelow">The speech threshold (SilenceRms): long silent edges are trimmed before upload. Null keeps everything.</param>
/// <param name="Live">The audio arrives while the key is held (a hold), so it can be streamed. False for a finished buffer (Retry).</param>
public sealed record SttSessionOptions(IReadOnlyList<string> Vocabulary, string? Language = null, float? TrimBelow = null, bool Live = false)
{
    /// <summary>The transcription response format; only the live tests ask for another (verbose_json, for its duration).</summary>
    internal string ResponseFormat { get; init; } = "json";
}

/// <summary>How the audio was sent: in one request at key-up, streamed while held, or streamed then re-sent in one.</summary>
public enum SttUpload { Batch, Streamed, StreamFellBack }

/// <param name="BodySentMs">When the upload finished, in ms after <c>CompleteAsync</c> started.</param>
/// <param name="FirstByteMs">When response headers arrived, in ms after <c>CompleteAsync</c> started.</param>
/// <param name="NewConnection">Whether this request had to open a new connection (cold start).</param>
/// <param name="UploadBytes">Audio bytes sent after silence trimming.</param>
public sealed record SttResult(string Text, long BodySentMs, long FirstByteMs, bool NewConnection, long UploadBytes = 0, SttUpload Upload = SttUpload.Batch);

public enum SttReadiness { Ready, MissingKey, InsecureEndpoint, InvalidEndpoint }

public enum SttError
{
    MissingKey,
    InsecureEndpoint,
    InvalidEndpoint,
    Auth,
    RateLimited,
    QuotaExceeded,
    BadRequest,
    Server,
    Redirected,
    MalformedResponse,
    Timeout,
    Network,
}

/// <summary>
/// An STT failure. The message is only the error's name and there is never an inner exception: HTTP exceptions
/// quote URLs and JSON exceptions quote the body, and either could end up in a crash log or the event log.
/// </summary>
public sealed class SttException(SttError error, int? httpStatus = null) : Exception(error.ToString())
{
    public SttError Error { get; } = error;
    public int? HttpStatus { get; } = httpStatus;

    /// <summary>How the failed request was sent, for the timing log.</summary>
    public SttUpload Upload { get; init; }
}

/// <summary>
/// A transcription in progress. Audio is pushed while the key is held, so a streaming implementation (PR7) can
/// start work before <see cref="CompleteAsync"/>.
/// </summary>
public interface ITranscriptionSession : IAsyncDisposable
{
    void PushAudio(ReadOnlyMemory<byte> pcm);

    Task<SttResult> CompleteAsync(CancellationToken ct);
}

public interface ITranscriptionSessionFactory
{
    SttProviderKind Provider { get; }

    /// <summary>Cheap preflight, called at key-down so "set up a key" shows before anything is recorded.</summary>
    SttReadiness CheckReady();

    ITranscriptionSession Create(SttSessionOptions options);
}

public interface IApiKeySource
{
    string? Get(string name);
}

/// <summary>Keys from environment variables only: test projects and the E2E runner. The app uses <c>KeySource</c>.</summary>
public sealed class EnvironmentApiKeySource : IApiKeySource
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
