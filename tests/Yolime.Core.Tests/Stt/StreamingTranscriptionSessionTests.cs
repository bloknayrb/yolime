using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Audio;
using Yolime.Core.Logging;
using Yolime.Core.Net;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Stt;

public class StreamingTranscriptionSessionTests
{
    private const float Threshold = 0.0025f;
    private static readonly SttEndpoint OpenAi = new(new Uri("https://api.openai.com/v1/"), "gpt-4o-transcribe", "sk-test", SttProviderKind.OpenAi);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly StreamingFakeHandler _fake = new();
    private readonly RecordingLog _log = new();
    private readonly FakeTimeProvider _time = new();

    private StreamingTranscriptionSession Session(long maxBytes = StreamingTranscriptionSession.MaxBytes) =>
        new(HttpClients.Create(_fake), OpenAi, new SttSessionOptions([], TrimBelow: Threshold, Live: true), _log, _time, maxBytes);

    private static byte[] Tone(int ms, double amp = 0.2)
    {
        var n = ms * AudioFormat.SampleRate / 1000;
        var b = new byte[n * 2];
        for (var i = 0; i < n; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 440 * i / AudioFormat.SampleRate) * amp * 32767));
        return b;
    }

    private static byte[] Silence(int ms) => new byte[ms * AudioFormat.BytesPerMs];

    private static void Push(ITranscriptionSession s, byte[] pcm)
    {
        for (var o = 0; o < pcm.Length; o += 3200) s.PushAudio(pcm.AsMemory(o, Math.Min(3200, pcm.Length - o)));
    }

    /// <summary>The PCM inside the multipart's WAV part (after its 44-byte header, before the next boundary).</summary>
    private static byte[] WavData(byte[] body)
    {
        var text = Encoding.Latin1.GetString(body);
        var riff = text.IndexOf("RIFF", StringComparison.Ordinal);
        var end = text.IndexOf("\r\n--", riff, StringComparison.Ordinal);
        return body[(riff + WavFile.HeaderBytes)..(end < 0 ? body.Length : end)];
    }

    private async Task WaitForRequests(int n)
    {
        for (var i = 0; i < 500 && _fake.Count < n; i++) await Task.Delay(10);
        Assert.Equal(n, _fake.Count);
    }

    [Fact]
    public async Task NothingIsSent_UntilTheSpeechGateHearsSpeech()
    {
        // A click or a breath isn't speech: 200 ms of sound is under the gate's 300 ms.
        await using var s = Session();
        Push(s, Silence(1500));
        Push(s, Tone(200));
        Push(s, Silence(500));
        await Task.Delay(100);
        Assert.Equal(0, _fake.Count);
    }

    [Fact]
    public async Task SilenceOnly_CompletesAsOneBatchRequest()
    {
        await using var s = Session();
        Push(s, Silence(2000));
        var r = await s.CompleteAsync(default);
        Assert.Equal(SttUpload.Batch, r.Upload);
        await WaitForRequests(1);
        Assert.False(_fake.Request(0).Chunked);
    }

    [Fact]
    public async Task Speech_IsStreamedWhileTheKeyIsStillHeld()
    {
        await using var s = Session();
        Push(s, Silence(2000));
        Push(s, Tone(1500));
        await WaitForRequests(1);
        var req = _fake.Request(0);
        Assert.True(req.Chunked);
        // The audio reaches the server before CompleteAsync is even called.
        await req.WaitForBytes(1500 * AudioFormat.BytesPerMs).WaitAsync(Wait);
        Assert.Equal(0, s.CompletedCount);
    }

    [Fact]
    public async Task TheStreamedAudio_IsExactlyWhatTheBatchTrimWouldSend()
    {
        var all = new List<byte>();
        await using var s = Session();
        foreach (var part in new[] { Silence(2500), Tone(1200), Silence(900), Tone(600), Silence(2600) })
        {
            Push(s, part);
            all.AddRange(part);
        }
        var r = await s.CompleteAsync(default);
        Assert.Equal(SttUpload.Streamed, r.Upload);
        Assert.Equal("hello", r.Text);
        var expected = SilenceTrim.Trim(all.ToArray(), Threshold).ToArray();
        Assert.Equal(expected, WavData(_fake.Request(0).Body));
        Assert.Equal(expected.Length, r.UploadBytes);
        Assert.True(await _fake.Request(0).Ended.Task);
    }

    [Fact]
    public async Task EachRoundOfNewAudio_IsFlushed()
    {
        // Without a flush, a chunk can sit in the connection's buffer and the overlap with speech shrinks.
        await using var s = Session();
        Push(s, Tone(1000));
        await WaitForRequests(1);
        await _fake.Request(0).WaitForBytes(900 * AudioFormat.BytesPerMs).WaitAsync(Wait);
        for (var i = 0; i < 200 && _fake.Request(0).Flushes == 0; i++) await Task.Delay(10); // the flush follows the write
        var before = _fake.Request(0).Flushes;
        Assert.True(before >= 1);
        Push(s, Tone(500));
        await _fake.Request(0).WaitForBytes(1400 * AudioFormat.BytesPerMs).WaitAsync(Wait);
        for (var i = 0; i < 200 && _fake.Request(0).Flushes == before; i++) await Task.Delay(10);
        Assert.True(_fake.Request(0).Flushes > before);
    }

    [Fact]
    public async Task DisposingWithoutCompleting_TearsTheBodyDown_ItNeverEnds()
    {
        // A discard, Esc or NoSpeech: the server must never get a complete file to transcribe.
        var s = Session();
        Push(s, Tone(1500));
        await WaitForRequests(1);
        await _fake.Request(0).WaitForBytes(1000 * AudioFormat.BytesPerMs).WaitAsync(Wait);
        await s.DisposeAsync();
        Assert.False(await _fake.Request(0).Ended.Task.WaitAsync(Wait));
        Assert.DoesNotContain("--\r\n", _fake.Request(0).BodyText[^20..]);
        Assert.Contains(LogEvent.StreamAborted, _log.Events);
    }

    [Fact]
    public async Task ANetworkErrorBeforeTheBodyEnds_FallsBackToOneBatchUpload()
    {
        _fake.DropAfterBytes = i => i == 0 ? 1000 * AudioFormat.BytesPerMs : null;
        _fake.Respond = _ => StreamingFakeHandler.Ok("from batch");
        await using var s = Session();
        Push(s, Tone(1500));
        await _fake.Dropped.Task.WaitAsync(Wait); // the drop comes while the key is still held
        var r = await s.CompleteAsync(default);
        Assert.Equal("from batch", r.Text);
        Assert.Equal(SttUpload.StreamFellBack, r.Upload);
        Assert.Equal(2, _fake.Count);
        Assert.False(_fake.Request(1).Chunked);
        Assert.Contains(LogEvent.StreamFellBack, _log.Events);
    }

    [Fact]
    public async Task ANetworkErrorAfterTheBodyEnded_IsReported_NeverReSent()
    {
        // The server had the whole file and may be transcribing it: a second upload could transcribe (and bill) it twice.
        _fake.Respond = _ => null;
        await using var s = Session();
        Push(s, Tone(1500));
        var ex = await Assert.ThrowsAsync<SttException>(() => s.CompleteAsync(default));
        Assert.Equal(SttError.Network, ex.Error);
        Assert.Equal(1, _fake.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.LengthRequired)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public async Task AStatusAboutTheStreamedForm_FallsBack(HttpStatusCode status)
    {
        _fake.Respond = i => i == 0 ? StreamingFakeHandler.Status(status) : StreamingFakeHandler.Ok("from batch");
        await using var s = Session();
        Push(s, Tone(1500));
        var r = await s.CompleteAsync(default);
        Assert.Equal(SttUpload.StreamFellBack, r.Upload);
        Assert.Equal("from batch", r.Text);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SttError.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, SttError.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, SttError.Server)]
    [InlineData(HttpStatusCode.NotFound, SttError.BadRequest)]
    public async Task OtherStatuses_AreReportedLikeBatch_WithoutASecondUpload(HttpStatusCode status, SttError error)
    {
        _fake.Respond = _ => StreamingFakeHandler.Status(status);
        await using var s = Session();
        Push(s, Tone(1500));
        var ex = await Assert.ThrowsAsync<SttException>(() => s.CompleteAsync(default));
        Assert.Equal(error, ex.Error);
        Assert.Equal(SttUpload.Streamed, ex.Upload);
        Assert.Equal(1, _fake.Count);
    }

    [Fact]
    public async Task AnEarlyAnswerMidSpeech_DoesNotBlockPushes_AndIsReportedAtComplete()
    {
        _fake.RespondEarly = i => StreamingFakeHandler.Status(HttpStatusCode.Unauthorized);
        await using var s = Session();
        Push(s, Tone(1000));
        await WaitForRequests(1);
        Push(s, Tone(3000)); // must return at once
        var ex = await Assert.ThrowsAsync<SttException>(() => s.CompleteAsync(default)).WaitAsync(Wait);
        Assert.Equal(SttError.Auth, ex.Error);
        Assert.Equal(1, _fake.Count);
    }

    [Fact]
    public async Task ACallerCancel_IsACancel_WithNoBatchUpload()
    {
        _fake.Respond = _ => { Thread.Sleep(50); return StreamingFakeHandler.Ok("late"); };
        await using var s = Session();
        Push(s, Tone(1500));
        await WaitForRequests(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.CompleteAsync(cts.Token));
        Assert.Equal(1, _fake.Count);
    }

    [Fact]
    public async Task PastTheByteCeiling_TheStreamIsAbandoned_AndCompleteUsesBatch()
    {
        await using var s = Session(maxBytes: 1000 * AudioFormat.BytesPerMs);
        Push(s, Tone(800));
        await WaitForRequests(1); // streaming, under the ceiling
        Push(s, Tone(700)); // past it, mid-stream
        Assert.False(await _fake.Request(0).Ended.Task.WaitAsync(Wait)); // torn down, never completed
        var r = await s.CompleteAsync(default);
        Assert.Equal(SttUpload.StreamFellBack, r.Upload);
        Assert.False(_fake.Request(_fake.Count - 1).Chunked);
        // The batch carries all of it, including what came after the stream was abandoned.
        Assert.Equal(1500 * AudioFormat.BytesPerMs, r.UploadBytes);
    }

    [Fact]
    public async Task ABodyThatFailsAfterTheHeaders_IsANetworkError_NotACrash()
    {
        // 200 headers, then the connection drops mid-JSON: reported like batch, and not re-sent (the body was whole).
        _fake.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) };
        await using var s = Session();
        Push(s, Tone(1500));
        var ex = await Assert.ThrowsAsync<SttException>(() => s.CompleteAsync(default));
        Assert.Equal(SttError.Network, ex.Error);
        Assert.Equal(1, _fake.Count);
    }

    private sealed class FailingStream : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("reset");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new HttpRequestException("reset");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ALongPause_ParksTheStream_AndSpeechAfterItRestartsFromTheBeginning()
    {
        // OpenAI drops a body that sits idle 15 s: held-back silence would kill the stream mid-hold.
        var all = new List<byte>();
        void Feed(StreamingTranscriptionSession s, byte[] pcm) { Push(s, pcm); all.AddRange(pcm); }
        await using var s = Session();
        Feed(s, Tone(1500));
        await WaitForRequests(1);
        for (var i = 0; i < 10; i++)
        {
            Feed(s, Silence(1000));
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        Feed(s, Silence(100)); // the push that notices the pause
        Assert.False(await _fake.Request(0).Ended.Task.WaitAsync(Wait)); // torn down, never completed
        Assert.Contains(LogEvent.StreamParked, _log.Events);

        Feed(s, Tone(800));
        await WaitForRequests(2);
        var r = await s.CompleteAsync(default);
        Assert.Equal(SttUpload.Streamed, r.Upload);
        Assert.Equal(SilenceTrim.Trim(all.ToArray(), Threshold).ToArray(), WavData(_fake.Request(1).Body));
    }

    [Fact]
    public async Task AHoldThatEndsInTheParkedSilence_IsSentAsOneBatchRequest()
    {
        await using var s = Session();
        Push(s, Tone(1500));
        await WaitForRequests(1);
        for (var i = 0; i < 10; i++)
        {
            Push(s, Silence(1000));
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        Push(s, Silence(100));
        var r = await s.CompleteAsync(default);
        Assert.Equal(SttUpload.StreamFellBack, r.Upload);
        Assert.Equal(2, _fake.Count);
        Assert.False(_fake.Request(1).Chunked);
    }

    [Fact]
    public async Task APushAfterComplete_IsIgnored()
    {
        await using var s = Session();
        Push(s, Tone(1500));
        await s.CompleteAsync(default);
        s.PushAudio(Tone(100));
        Assert.Equal(1, _fake.Count);
    }

    [Fact]
    public void Factory_StreamsOnlyLiveOpenAiSessions_WithTheSettingOn()
    {
        var keys = new DictionaryKeySource(new() { ["OPENAI_API_KEY"] = "sk", ["GROQ_API_KEY"] = "gsk" });
        ITranscriptionSessionFactory F(YolimeSettings s) => new BatchTranscriptionSessionFactory(() => s, keys, HttpClients.Create(_fake), _log, _time);
        var on = new YolimeSettings { StreamingUpload = true };
        Assert.IsType<StreamingTranscriptionSession>(F(on).Create(new SttSessionOptions([], Live: true)));
        Assert.IsType<BatchTranscriptionSession>(F(on).Create(new SttSessionOptions([], Live: false))); // Retry: a finished buffer
        Assert.IsType<BatchTranscriptionSession>(F(new YolimeSettings()).Create(new SttSessionOptions([], Live: true))); // off by default
        Assert.IsType<BatchTranscriptionSession>(F(on with { SttProvider = SttProviderKind.Groq }).Create(new SttSessionOptions([], Live: true)));
    }

    private sealed class DictionaryKeySource(Dictionary<string, string> keys) : IApiKeySource
    {
        public string? Get(string name) => keys.GetValueOrDefault(name);
    }
}
