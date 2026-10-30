using System.Net;
using System.Text;
using Yolime.Core.Audio;
using Yolime.Core.Net;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Stt;

public class BatchTranscriptionSessionTests
{
    private static readonly SttEndpoint OpenAi = new(new Uri("https://api.openai.com/v1/"), "gpt-4o-transcribe", "sk-test", SttProviderKind.OpenAi);
    private static readonly SttEndpoint Groq = new(new Uri("https://api.groq.com/openai/v1/"), "whisper-large-v3-turbo", "gsk-test", SttProviderKind.Groq);

    private static async Task<SttResult> Run(FakeHttpHandler fake, SttEndpoint? ep = null, IReadOnlyList<string>? vocab = null, byte[]? pcm = null, CancellationToken ct = default)
    {
        await using var session = new BatchTranscriptionSession(HttpClients.Create(fake), ep ?? OpenAi, new SttSessionOptions(vocab ?? []), new RecordingLog());
        session.PushAudio(pcm ?? new byte[3200]);
        return await session.CompleteAsync(ct);
    }

    private static string BodyText(FakeHttpHandler fake) => Encoding.UTF8.GetString(fake.Requests.Single().Body);

    private static async Task<SttError> ErrorOf(FakeHttpHandler fake, SttEndpoint? ep = null) =>
        (await Assert.ThrowsAsync<SttException>(() => Run(fake, ep))).Error;

    [Fact]
    public async Task Multipart_HasModelFileJsonFormat()
    {
        var fake = FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"hello"}""");
        var result = await Run(fake);

        Assert.Equal("hello", result.Text);
        var (req, _) = fake.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", req.RequestUri!.ToString());
        Assert.StartsWith("multipart/form-data", req.Content!.Headers.ContentType!.ToString());
        var body = BodyText(fake);
        Assert.Contains("name=model", body);
        Assert.Contains("gpt-4o-transcribe", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("filename=audio.wav", body);
        Assert.Contains("audio/wav", body);
    }

    [Fact]
    public async Task LongSilentEdges_AreTrimmedBeforeUpload()
    {
        // Three seconds of silence, half a second of tone, three more of silence.
        var pcm = new byte[(6500) * Yolime.Core.Audio.AudioFormat.BytesPerMs];
        var start = 3000 * Yolime.Core.Audio.AudioFormat.BytesPerMs;
        for (var i = 0; i < 500 * Yolime.Core.Audio.AudioFormat.SampleRate / 1000; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * i / Yolime.Core.Audio.AudioFormat.SampleRate) * 8000);
            pcm[start + i * 2] = (byte)v;
            pcm[start + i * 2 + 1] = (byte)(v >> 8);
        }
        var fake = FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"hi"}""");
        await using var session = new BatchTranscriptionSession(HttpClients.Create(fake), OpenAi, new SttSessionOptions([], TrimBelow: 0.0025f), new RecordingLog());
        session.PushAudio(pcm);
        await session.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.InRange(session.UploadBytes, 1800 * Yolime.Core.Audio.AudioFormat.BytesPerMs, 2000 * Yolime.Core.Audio.AudioFormat.BytesPerMs);
        Assert.True(fake.Requests.Single().Body.Length < pcm.Length / 3);
    }

    [Fact]
    public async Task Vocabulary_AsPrompt_OmittedWhenEmpty()
    {
        var withVocab = FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"x"}""");
        await Run(withVocab, vocab: ["Kubernetes", "Zoë"]);
        Assert.Contains("name=prompt", BodyText(withVocab));
        Assert.Contains("Kubernetes, Zoë", BodyText(withVocab));

        var without = FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"x"}""");
        await Run(without);
        Assert.DoesNotContain("name=prompt", BodyText(without));
    }

    [Fact]
    public async Task Wav_HasHeaderAndAllBytes()
    {
        var pcm = Enumerable.Range(0, 6400).Select(i => (byte)(i * 7)).ToArray();
        var fake = FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"x"}""");
        await Run(fake, pcm: pcm);

        var body = fake.Requests.Single().Body;
        var riff = body.AsSpan().IndexOf("RIFF"u8);
        Assert.True(riff >= 0);
        var wav = body.AsSpan(riff, WavFile.HeaderBytes + pcm.Length).ToArray();
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, wav);
            Assert.Equal(pcm, WavFile.ReadPcm(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OpenAiKey_OnlyToOpenAiHost()
    {
        var fake = FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"x"}""");
        await Run(fake);
        var req = fake.Requests.Single().Request;
        Assert.Equal("api.openai.com", req.RequestUri!.Host);
        Assert.Equal("Bearer sk-test", req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GroqKey_OnlyToGroqHost()
    {
        var fake = FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"x"}""");
        await Run(fake, Groq);
        var req = fake.Requests.Single().Request;
        Assert.Equal("api.groq.com", req.RequestUri!.Host);
        Assert.Equal("Bearer gsk-test", req.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SttError.Auth)]
    [InlineData(HttpStatusCode.Forbidden, SttError.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, SttError.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, SttError.Server)]
    [InlineData(HttpStatusCode.BadRequest, SttError.BadRequest)]
    [InlineData(HttpStatusCode.TemporaryRedirect, SttError.Redirected)]
    public async Task Status_MapsToError(HttpStatusCode status, SttError expected)
    {
        var ex = await Assert.ThrowsAsync<SttException>(() => Run(FakeHttpHandler.Json(status, """{"error":{"code":"x"}}""")));
        Assert.Equal(expected, ex.Error);
        Assert.Equal((int)status, ex.HttpStatus);
    }

    [Fact]
    public async Task Status429InsufficientQuota_QuotaExceeded() =>
        Assert.Equal(SttError.QuotaExceeded, await ErrorOf(FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"insufficient_quota","message":"x"}}""")));

    [Fact]
    public async Task Status200MalformedJson_MalformedResponse() =>
        Assert.Equal(SttError.MalformedResponse, await ErrorOf(FakeHttpHandler.Json(HttpStatusCode.OK, "{not json SENTINEL")));

    [Fact]
    public async Task HttpRequestException_Network() =>
        Assert.Equal(SttError.Network, await ErrorOf(FakeHttpHandler.Throws(new HttpRequestException("SENTINEL down"))));

    [Fact]
    public async Task ConnectTimeout_Timeout() =>
        // SocketsHttpHandler reports its ConnectTimeout as a cancellation the caller didn't ask for.
        Assert.Equal(SttError.Timeout, await ErrorOf(FakeHttpHandler.Throws(new TaskCanceledException("connect timeout"))));

    [Fact]
    public async Task CallerCancel_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Run(FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"x"}"""), ct: cts.Token));
    }

    [Fact]
    public async Task HostPinning_ForeignHost_InvalidEndpoint()
    {
        // A request whose URI host differs from the endpoint it was built for never leaves the process.
        var fake = FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"x"}""");
        var client = HttpClients.Create(fake);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://evil.example/v1/audio/transcriptions");
        HostPinningHandler.Expect(request, "api.openai.com");
        await Assert.ThrowsAsync<HostPinningException>(() => client.SendAsync(request));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task HostPinning_MissingExpectation_Refused()
    {
        var fake = FakeHttpHandler.Json(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<HostPinningException>(() => HttpClients.Create(fake).GetAsync("https://api.openai.com/v1/models"));
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Exception_ToString_NeverContainsBodyOrUri(HttpStatusCode status)
    {
        var custom = new SttEndpoint(new Uri("https://sentinel-host.example/v1/"), "m", "SENTINEL_KEY", SttProviderKind.Custom);
        var body = status == HttpStatusCode.OK ? "SENTINEL_BODY not json" : """{"error":{"message":"SENTINEL_BODY"}}""";
        var ex = await Assert.ThrowsAsync<SttException>(() => Run(FakeHttpHandler.Json(status, body), custom));
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("SENTINEL", ex.ToString());
        Assert.DoesNotContain("sentinel-host", ex.ToString());
    }
}
