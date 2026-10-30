using System.Diagnostics;
using System.Net;
using Yolime.Core.Audio;
using Yolime.Core.Logging;
using Yolime.Core.Net;
using Yolime.Core.Settings;

namespace Yolime.Core.Stt;

public enum StreamFallback { Network, Status, Ceiling }

/// <summary>When a hold's audio may be streamed: the setting is on and the provider is one verified to accept it.</summary>
public static class SttStreaming
{
    /// <summary>
    /// OpenAI only: a chunked upload with a WAV header of unknown length was verified live on gpt-4o-transcribe and
    /// whisper-1 (2026-10-28). Groq and custom endpoints keep the batch upload until they are.
    /// </summary>
    public static bool Applies(YolimeSettings settings) => settings.StreamingUpload && settings.SttProvider == SttProviderKind.OpenAi;
}

/// <summary>
/// Sends the recording while the key is held, so key-up waits only for the server. The request opens once the speech
/// gate hears speech (a breath or a click never opens one), and sends exactly what <see cref="SilenceTrim"/> would keep,
/// silence after the last sound held back until sound resumes. Every byte is also kept locally: anything that goes
/// wrong with the stream before its body is complete falls back to one batch upload, and a disposal without completion
/// tears the request down so the server never gets a complete file.
/// </summary>
public sealed class StreamingTranscriptionSession : ITranscriptionSession
{
    /// <summary>Six minutes of audio: the five-minute hands-free cap and some margin.</summary>
    public const long MaxBytes = 6 * 60 * 1000L * AudioFormat.BytesPerMs;

    /// <summary>
    /// How long a stream may sit with nothing new to send (silence held back) before it's parked. OpenAI drops a request
    /// body idle for 15 s or more (10 s survived; measured live 2026-10-28), so a long pause would otherwise cost a failed
    /// stream and a batch upload. A parked stream is torn down like a discarded one, and a fresh request re-sends from the
    /// start when speech resumes.
    /// </summary>
    public static readonly TimeSpan ParkAfter = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly SttEndpoint _endpoint;
    private readonly SttSessionOptions _options;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly long _maxBytes;
    private readonly StreamTrimmer _trim;
    private readonly SpeechGate _gate;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private StreamedWav? _body;
    private Task<HttpResponseMessage>? _send;
    private HttpRequestMessage? _request;
    private CancellationTokenSource? _attempt;
    private bool _abandoned;
    private int _completed;
    private long _lastSendable;
    private long _lastGrowthAt;
    private long? _parkedAt;
    private Task<HttpResponseMessage>? _abandonedSend;
    private volatile bool _disposed;

    public StreamingTranscriptionSession(HttpClient http, SttEndpoint endpoint, SttSessionOptions options, ILog log, TimeProvider time, long maxBytes = MaxBytes)
    {
        _http = http;
        _endpoint = endpoint;
        _options = options;
        _log = log;
        _time = time;
        _maxBytes = maxBytes;
        var threshold = options.TrimBelow ?? 0.0025f;
        _trim = new StreamTrimmer(threshold);
        _gate = new SpeechGate(threshold);
    }

    /// <summary>For tests: how many times CompleteAsync has been entered.</summary>
    internal int CompletedCount => Volatile.Read(ref _completed);

    /// <summary>Never throws or blocks: the capture loop calls it with every chunk.</summary>
    public void PushAudio(ReadOnlyMemory<byte> pcm)
    {
        // Not the cancellation: an abandoned stream still needs every byte, for the batch upload that replaces it.
        if (Volatile.Read(ref _completed) > 0 || _disposed) return;
        try
        {
            _trim.Push(pcm.Span);
            _gate.Add(new AudioChunk(pcm, Rms.Of(pcm.Span)));
            lock (_lock)
            {
                if (_abandoned) return;
                if (_trim.Length > _maxBytes)
                {
                    Abandon(StreamFallback.Ceiling);
                    return;
                }
                var now = _time.GetTimestamp();
                var sendable = _trim.SendableEnd;
                if (sendable > _lastSendable)
                {
                    _lastSendable = sendable;
                    _lastGrowthAt = now;
                }
                // Starts at the first speech; after a park, again once there's something new to send.
                if (_body is null && _gate.HeardSpeech && _trim.Start is not null && (_parkedAt is not { } parked || sendable > parked)) Start();
                else if (_body is not null && _time.GetElapsedTime(_lastGrowthAt, now) >= ParkAfter) Park(sendable);
                _body?.Signal();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_lock) Abandon(StreamFallback.Network);
        }
    }

    // Under _lock. Each attempt has its own token, under the session's: parking one leaves the session alive.
    private void Start()
    {
        _attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var token = _attempt.Token;
        _body = new StreamedWav(_trim, _time, token);
        _request = BatchTranscriptionSession.Request(BatchTranscriptionSession.Form(_body, _endpoint, _options), _endpoint);
        var request = _request;
        _lastGrowthAt = _time.GetTimestamp();
        // Off the capture path; its fault is always observed (awaited in Complete or Dispose, or by this continuation).
        _send = Task.Run(() => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token));
        _ = _send.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    // Under _lock. Torn down like a discard (its body never ends); the next speech starts a fresh request.
    private void Park(long sendable)
    {
        _log.Write(LogEvent.StreamParked, LogField.Count("bytes", _body!.Sent));
        _attempt!.Cancel();
        _body.Abort();
        _body = null;
        _send = null;
        _parkedAt = sendable;
    }

    // Under _lock. The status or the exception type (never its message) says why, for the field.
    private void Abandon(StreamFallback reason, int? status = null, Exception? error = null)
    {
        if (_abandoned) return;
        _abandoned = true;
        if (status is { } s) _log.Write(LogEvent.StreamFellBack, LogField.Tag("reason", reason), LogField.HttpStatus(s));
        else if (error is not null) _log.Write(LogEvent.StreamFellBack, LogField.Tag("reason", reason), LogField.Error(error));
        else _log.Write(LogEvent.StreamFellBack, LogField.Tag("reason", reason));
        if (_body is null) return;
        // Cancel first, so the body throws rather than ending: the server must never see a complete file.
        _abandonedSend = _send;
        _attempt?.Cancel();
        _body.Abort();
    }

    public async Task<SttResult> CompleteAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _completed);
        var start = _time.GetTimestamp();
        Task<HttpResponseMessage>? send;
        StreamedWav? body;
        lock (_lock)
        {
            send = _abandoned ? null : _send;
            body = _abandoned ? null : _body;
            body?.Finish(_trim.Final());
        }
        // Never started (no speech), abandoned, or parked in the silence the hold ended on: one batch upload, once the
        // torn-down stream has unwound, so the two never overlap.
        if (send is null || body is null)
        {
            await QuietlyAsync(_abandonedSend);
            return await BatchAsync(ct, fellBack: _abandoned || _parkedAt is not null, start);
        }

        // The caller's cancel (a tap, or the pipeline's timeout) tears the stream down at once.
        using var forward = ct.Register(() => _cts.Cancel());
        HttpResponseMessage response;
        try
        {
            response = await send.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HostPinningException)
        {
            throw Fail(SttError.InvalidEndpoint, null, SttUpload.Streamed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // A connection failure or the connect timeout. Before the body was complete the server has nothing to
            // transcribe, so one batch upload is safe; after it, the server may be transcribing it already.
            if (!body.Completed) return await FallBackAsync(StreamFallback.Network, ct, start, error: ex);
            throw Fail(ex is OperationCanceledException ? SttError.Timeout : SttError.Network, null, SttUpload.Streamed);
        }

        using (response)
        {
            var firstByteMs = Ms(start);
            var status = (int)response.StatusCode;
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // The body was whole and the server answered: a dropped reply is reported, never re-sent.
                throw Fail(SttError.Network, status, SttUpload.Streamed);
            }
            if (!response.IsSuccessStatusCode)
            {
                // These say the streamed form wasn't taken (nothing was transcribed): send it once the usual way.
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestTimeout or HttpStatusCode.LengthRequired
                    or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType)
                {
                    return await FallBackAsync(StreamFallback.Status, ct, start, status);
                }
                throw Fail(BatchTranscriptionSession.MapStatus(response.StatusCode, text), status, SttUpload.Streamed);
            }
            var parsed = BatchTranscriptionSession.ParseText(text) ?? throw Fail(SttError.MalformedResponse, status, SttUpload.Streamed);
            var sent = body.Sent;
            _log.Write(LogEvent.StageCompleted, LogField.Tag("stage", Stage.Stt), LogField.HttpStatus(status), LogField.Count("audioBytes", sent));
            var bodyMs = body.CompletedAt is { } at ? Math.Max(0, (long)_time.GetElapsedTime(start, at).TotalMilliseconds) : firstByteMs;
            return new SttResult(parsed, bodyMs, firstByteMs, HttpClients.OpenedNewConnection(_request!), sent, SttUpload.Streamed);
        }
    }

    private async Task<SttResult> FallBackAsync(StreamFallback reason, CancellationToken ct, long start, int? status = null, Exception? error = null)
    {
        lock (_lock) Abandon(reason, status, error);
        await QuietlyAsync(_send);
        return await BatchAsync(ct, fellBack: true, start);
    }

    /// <summary>One batch upload of the whole recording, to the endpoint captured at the start, in the same budget.</summary>
    private async Task<SttResult> BatchAsync(CancellationToken ct, bool fellBack, long start)
    {
        await using var batch = new BatchTranscriptionSession(_http, _endpoint, _options, _log);
        batch.PushAudio(_trim.All());
        var upload = fellBack ? SttUpload.StreamFellBack : SttUpload.Batch;
        // Measured from this CompleteAsync, so a fallback's times include what the failed stream cost.
        var before = Ms(start);
        try
        {
            var r = await batch.CompleteAsync(ct);
            return r with { Upload = upload, BodySentMs = r.BodySentMs + before, FirstByteMs = r.FirstByteMs + before };
        }
        catch (SttException ex)
        {
            throw new SttException(ex.Error, ex.HttpStatus) { Upload = upload };
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<HttpResponseMessage>? send;
        _disposed = true;
        lock (_lock)
        {
            send = _send;
            if (Volatile.Read(ref _completed) == 0 && _body is not null && !_abandoned)
            {
                // A discard, Esc or NoSpeech: tear the request down so its body never ends.
                _log.Write(LogEvent.StreamAborted, LogField.Count("bytes", _body.Sent));
                _cts.Cancel();
                _body.Abort();
            }
            else
            {
                _cts.Cancel();
            }
        }
        await QuietlyAsync(send);
        _request?.Dispose();
        _attempt?.Dispose();
        _cts.Dispose();
    }

    /// <summary>Waits a short while for an aborted send to unwind; its fault is already observed.</summary>
    private static async Task QuietlyAsync(Task<HttpResponseMessage>? send)
    {
        if (send is null) return;
        try
        {
            using var r = await send.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception) { }
    }

    private long Ms(long start) => (long)_time.GetElapsedTime(start).TotalMilliseconds;

    private SttException Fail(SttError error, int? status, SttUpload upload)
    {
        if (status is { } s) _log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Stt), LogField.Tag("sttError", error), LogField.HttpStatus(s));
        else _log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Stt), LogField.Tag("sttError", error));
        return new SttException(error, status) { Upload = upload };
    }

    /// <summary>
    /// The WAV part, written as the recording grows: an unknown-length header, then the trimmer's sendable bytes, each
    /// flushed. It reads the trimmer by offset, so a resend on a fresh connection starts over correctly. Finish lets it
    /// write up to the final end and return; Abort makes it throw, so the request never completes.
    /// </summary>
    private sealed class StreamedWav(StreamTrimmer trim, TimeProvider time, CancellationToken token) : HttpContent
    {
        private readonly object _gate = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long? _end;
        private bool _aborted;
        private long _sent;
        private long? _completedAt;

        public bool Completed { get; private set; }

        public long Sent => Interlocked.Read(ref _sent);

        public long? CompletedAt
        {
            get { lock (_gate) return _completedAt; }
        }

        public void Signal()
        {
            TaskCompletionSource old;
            lock (_gate)
            {
                old = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            old.TrySetResult();
        }

        public void Finish((long Start, long End) range)
        {
            lock (_gate) _end = range.End;
            Signal();
        }

        public void Abort()
        {
            lock (_gate) _aborted = true;
            Signal();
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            await stream.WriteAsync(WavFile.StreamingHeader(), linked.Token);
            var offset = trim.Start ?? 0;
            var buffer = new byte[32 * 1024];
            Interlocked.Exchange(ref _sent, 0);
            while (true)
            {
                Task changed;
                long limit;
                lock (_gate)
                {
                    if (_aborted) throw new OperationCanceledException(token);
                    changed = _changed.Task;
                    limit = _end ?? trim.SendableEnd;
                }
                while (offset < limit)
                {
                    var n = trim.CopyTo(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, limit - offset)));
                    if (n == 0) break;
                    await stream.WriteAsync(buffer.AsMemory(0, n), linked.Token);
                    offset += n;
                    Interlocked.Add(ref _sent, n);
                }
                await stream.FlushAsync(linked.Token);
                lock (_gate)
                {
                    if (_aborted) throw new OperationCanceledException(token);
                    if (_end is { } end && offset >= end)
                    {
                        Completed = true;
                        _completedAt = time.GetTimestamp();
                        return;
                    }
                }
                await changed.WaitAsync(linked.Token);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
