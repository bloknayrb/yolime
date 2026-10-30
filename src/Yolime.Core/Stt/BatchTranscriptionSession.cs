using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Yolime.Core.Audio;
using Yolime.Core.Logging;
using Yolime.Core.Net;
using Yolime.Core.Settings;

namespace Yolime.Core.Stt;

/// <summary>
/// Buffers the recording and uploads it in one multipart request to an OpenAI-compatible
/// <c>/audio/transcriptions</c> endpoint (OpenAI, Groq, or a custom https one).
/// </summary>
public sealed class BatchTranscriptionSession(HttpClient http, SttEndpoint endpoint, SttSessionOptions options, ILog log) : ITranscriptionSession
{
    private readonly MemoryStream _pcm = new();

    /// <summary>The audio bytes actually sent, after trimming.</summary>
    public long UploadBytes { get; private set; }

    public void PushAudio(ReadOnlyMemory<byte> pcm) => _pcm.Write(pcm.Span);

    public async Task<SttResult> CompleteAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var request = BuildRequest(clock, out var content);
        int? status = null;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var firstByteMs = clock.ElapsedMilliseconds;
            status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new SttException(MapStatus(response.StatusCode, body), status);

            var text = ParseText(body) ?? throw new SttException(SttError.MalformedResponse, status);
            log.Write(LogEvent.StageCompleted, LogField.Tag("stage", Stage.Stt), LogField.HttpStatus(status.Value), LogField.Count("audioBytes", UploadBytes));
            return new SttResult(text, content.SentMs ?? firstByteMs, firstByteMs, HttpClients.OpenedNewConnection(request), UploadBytes);
        }
        catch (SttException ex)
        {
            LogFailure(ex.Error, status);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled (user cancel, or the pipeline's own timeout): it decides which one this was.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Not the caller's token, so the handler's ConnectTimeout fired.
            throw Fail(SttError.Timeout, status);
        }
        catch (HostPinningException)
        {
            throw Fail(SttError.InvalidEndpoint, status);
        }
        catch (HttpRequestException)
        {
            throw Fail(SttError.Network, status);
        }
    }

    private HttpRequestMessage BuildRequest(Stopwatch clock, out TimedContent content)
    {
        ReadOnlyMemory<byte> pcm = _pcm.GetBuffer().AsMemory(0, (int)_pcm.Length);
        if (options.TrimBelow is { } threshold) pcm = SilenceTrim.Trim(pcm, threshold);
        UploadBytes = pcm.Length;
        var wav = new MemoryStream(pcm.Length + WavFile.HeaderBytes);
        WavFile.WriteHeader(wav, pcm.Length);
        wav.Write(pcm.Span);
        wav.Position = 0;
        content = new TimedContent(Form(new StreamContent(wav), endpoint, options), clock);
        return Request(content, endpoint);
    }

    /// <summary>The transcription form, with the audio as its file part (batch or streamed).</summary>
    internal static MultipartFormDataContent Form(HttpContent file, SttEndpoint endpoint, SttSessionOptions options)
    {
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var form = new MultipartFormDataContent
        {
            { file, "file", "audio.wav" },
            { new StringContent(endpoint.Model), "model" },
            { new StringContent(options.ResponseFormat), "response_format" },
        };
        if (options.Vocabulary.Count > 0) form.Add(new StringContent(string.Join(", ", options.Vocabulary)), "prompt");
        if (options.Language is { Length: > 0 } language) form.Add(new StringContent(language), "language");
        return form;
    }

    /// <summary>The request for that form: the endpoint's key, pinned to the endpoint's host.</summary>
    internal static HttpRequestMessage Request(HttpContent content, SttEndpoint endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUrl, "audio/transcriptions")) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        HostPinningHandler.Expect(request, endpoint.BaseUrl.Host);
        return request;
    }

    internal static SttError MapStatus(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SttError.Auth,
        // OpenAI answers 429 both for rate limits and for an exhausted balance; only the error code tells them apart.
        HttpStatusCode.TooManyRequests => ErrorCode(body) == "insufficient_quota" ? SttError.QuotaExceeded : SttError.RateLimited,
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => SttError.Redirected,
        >= HttpStatusCode.InternalServerError => SttError.Server,
        _ => SttError.BadRequest,
    };

    private static string? ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string? ParseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private SttException Fail(SttError error, int? status)
    {
        LogFailure(error, status);
        return new SttException(error, status);
    }

    private void LogFailure(SttError error, int? status)
    {
        if (status is { } s) log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Stt), LogField.Tag("sttError", error), LogField.HttpStatus(s));
        else log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Stt), LogField.Tag("sttError", error));
    }

    public ValueTask DisposeAsync()
    {
        _pcm.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Records when the request body has been fully written, to split upload time from server time.</summary>
    private sealed class TimedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Stopwatch _clock;

        public TimedContent(HttpContent inner, Stopwatch clock)
        {
            _inner = inner;
            _clock = clock;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        public long? SentMs { get; private set; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await _inner.CopyToAsync(stream, context);
            SentMs = _clock.ElapsedMilliseconds;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? -1;
            return length >= 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Batch sessions, or streaming ones where streaming applies (<see cref="SttStreaming.Applies"/>).</summary>
public sealed class BatchTranscriptionSessionFactory(Func<YolimeSettings> settings, IApiKeySource keys, HttpClient http, ILog log, TimeProvider? time = null) : ITranscriptionSessionFactory
{
    public SttProviderKind Provider => settings().SttProvider;

    public SttReadiness CheckReady()
    {
        SttEndpoint.TryResolve(settings(), keys, out _, out var readiness);
        return readiness;
    }

    public ITranscriptionSession Create(SttSessionOptions options)
    {
        var current = settings();
        var endpoint = SttEndpoint.Resolve(current, keys);
        return SttStreaming.Applies(current) && options.Live
            ? new StreamingTranscriptionSession(http, endpoint, options, log, time ?? TimeProvider.System)
            : new BatchTranscriptionSession(http, endpoint, options, log);
    }
}
