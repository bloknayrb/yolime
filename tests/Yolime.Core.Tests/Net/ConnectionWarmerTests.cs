using System.Net;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Net;
using Yolime.Core.Settings;
using Yolime.Core.Tests.Stt;

namespace Yolime.Core.Tests.Net;

public class ConnectionWarmerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    private (ConnectionWarmer Warmer, FakeHttpHandler Handler) Make()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.Unauthorized, "{}");
        return (new ConnectionWarmer(HttpClients.Create(handler), _time, new RecordingLog()), handler);
    }

    [Fact]
    public async Task AHold_WarmsTheSttAndClaudeHosts_WithNoKey()
    {
        var (w, h) = Make();
        await w.WarmAsync(new YolimeSettings());
        var hosts = h.Requests.Select(r => r.Request.RequestUri!.Host).Order().ToList();
        Assert.Equal(["api.anthropic.com", "api.openai.com"], hosts);
        foreach (var (req, _) in h.Requests)
        {
            Assert.Equal(HttpMethod.Head, req.Method);
            // Keyless by construction: a warm-up only opens the TLS connection the real request will reuse.
            Assert.Null(req.Headers.Authorization);
            Assert.False(req.Headers.Contains("x-api-key"));
        }
    }

    [Fact]
    public async Task AHostUsedRecently_IsLeftAlone()
    {
        var (w, h) = Make();
        await w.WarmAsync(new YolimeSettings());
        var first = h.Requests.Count;
        _time.Advance(TimeSpan.FromSeconds(10));
        await w.WarmAsync(new YolimeSettings());
        Assert.Equal(first, h.Requests.Count);
        _time.Advance(ConnectionWarmer.IdleAfter);
        await w.WarmAsync(new YolimeSettings());
        Assert.Equal(first * 2, h.Requests.Count);
    }

    [Fact]
    public async Task WhileStreaming_OpenAisSttHostIsLeftToTheStream_OtherProvidersAreStillWarmed()
    {
        // A streamed upload opens its connection while the user speaks, so warming it would only add a socket.
        var (w, h) = Make();
        await w.WarmAsync(new YolimeSettings { StreamingUpload = true });
        Assert.Equal(["api.anthropic.com"], h.Requests.Select(r => r.Request.RequestUri!.Host));
        var (g, gh) = Make();
        await g.WarmAsync(new YolimeSettings { StreamingUpload = true, SttProvider = SttProviderKind.Groq });
        Assert.Contains("api.groq.com", gh.Requests.Select(r => r.Request.RequestUri!.Host));
    }

    [Fact]
    public async Task ARealRequest_CountsAsUse()
    {
        var (w, h) = Make();
        w.MarkUsed("api.openai.com");
        await w.WarmAsync(new YolimeSettings());
        Assert.Equal(["api.anthropic.com"], h.Requests.Select(r => r.Request.RequestUri!.Host));
    }

    [Theory]
    [InlineData(SttProviderKind.Groq, "api.groq.com")]
    [InlineData(SttProviderKind.Custom, "stt.example.com")]
    public async Task TheConfiguredSttHost_IsTheOneWarmed(SttProviderKind kind, string host)
    {
        var (w, h) = Make();
        await w.WarmAsync(new YolimeSettings { SttProvider = kind, CustomSttBaseUrl = "https://stt.example.com/v1", CustomSttModel = "m" });
        Assert.Contains(h.Requests, r => r.Request.RequestUri!.Host == host);
    }

    [Fact]
    public async Task AnInvalidCustomEndpoint_IsNotWarmed()
    {
        var (w, h) = Make();
        await w.WarmAsync(new YolimeSettings { SttProvider = SttProviderKind.Custom, CustomSttBaseUrl = "http://stt.example.com/v1", CustomSttModel = "m" });
        Assert.Equal(["api.anthropic.com"], h.Requests.Select(r => r.Request.RequestUri!.Host));
    }

    [Fact]
    public async Task AFailedWarmUp_IsQuiet()
    {
        var w = new ConnectionWarmer(HttpClients.Create(FakeHttpHandler.Throws(new HttpRequestException("offline"))), _time, new RecordingLog());
        await w.WarmAsync(new YolimeSettings()); // no exception: warming is best effort
    }
}
