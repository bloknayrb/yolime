using System.Net;
using System.Text;
using System.Text.Json;
using Yolime.Core.Llm;
using Yolime.Core.Modes;
using Yolime.Core.Net;
using Yolime.Core.Pipeline;
using Yolime.Core.Prompts;
using Yolime.Core.Settings;
using Yolime.Core.Stt;
using Yolime.Core.Tests.Stt;

namespace Yolime.Core.Tests.Llm;

public sealed class ClaudeRewriterTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly RecordingLog _log = new();
    private YolimeSettings _settings = new();
    private string? _key = "test-key";

    public void Dispose() => _root.Dispose();

    private sealed class Keys(Func<string?> key) : IApiKeySource
    {
        public List<string> Asked { get; } = [];
        public string? Get(string name)
        {
            Asked.Add(name);
            return name == ClaudeRewriter.KeyName ? key() : "WRONG_KEY_" + name;
        }
    }

    private ClaudeRewriter Rewriter(HttpMessageHandler handler, Keys? keys = null) =>
        new(HttpClients.Create(handler), keys ?? new Keys(() => _key), new PromptBuilder(new PromptLibrary(_root.Paths)), () => _settings, _log);

    internal static string Sse(params string[] events) => string.Concat(events.Select(e => $"event: x\ndata: {e}\n\n"));

    internal static string Delta(string text) =>
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""" + JsonSerializer.Serialize(text) + "}}";

    internal static string Stop(string reason, string? category = null) =>
        """{"type":"message_delta","delta":{"stop_reason":""" + JsonSerializer.Serialize(reason) + ""","stop_details":"""
        + (category is null ? "null" : """{"category":""" + JsonSerializer.Serialize(category) + "}")
        + """},"usage":{"output_tokens":3}}""";

    internal const string Start = """{"type":"message_start","message":{"id":"m","model":"claude-haiku-5-5","content":[]}}""";
    internal const string BlockStart = """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""";
    internal const string End = """{"type":"message_stop"}""";

    internal static FakeHttpHandler Ok(params string[] events) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse(events), Encoding.UTF8, "text/event-stream") });

    private static FakeHttpHandler Status(HttpStatusCode status, string? retryAfter = null) => new(_ =>
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent("""{"type":"error","error":{"type":"x","message":"SENTINEL_BODY"}}""") };
        if (retryAfter is not null) r.Headers.TryAddWithoutValidation("retry-after", retryAfter);
        return r;
    });

    private static RewriteRequest Req(Mode mode = Mode.Dictate, string? context = null) =>
        new(mode, "hello there", context is null ? null : new CapturedContext(context, ContextSource.Selection, 0), Profile.Casual);

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var sb = new StringBuilder();
        await foreach (var d in stream) sb.Append(d);
        return sb.ToString();
    }

    private static JsonElement Body(FakeHttpHandler h, int i = 0) => JsonDocument.Parse(h.Requests[i].Body).RootElement;

    [Fact]
    public async Task Streams_TextDeltas_InOrder()
    {
        var h = Ok(Start, BlockStart, """{"type":"ping"}""", Delta("Hel"), Delta("lo."), Stop("end_turn"), End);
        Assert.Equal("Hello.", await Collect(Rewriter(h).StreamAsync(Req(), default)));
    }

    public static TheoryData<Mode, string, string> Shapes() => new()
    {
        { Mode.Dictate, ModelIds.Haiku, "disabled" },
        { Mode.Prompt, ModelIds.Haiku, "disabled" },
        { Mode.Prompt, ModelIds.Sonnet, "between_tools" },
        { Mode.Reply, ModelIds.Sonnet, "between_tools" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task RequestShape_PerModelAndMode(Mode mode, string model, string thinking)
    {
        if (mode == Mode.Prompt) _settings = _settings with { PromptModel = model };
        var h = Ok(Start, Delta("x"), Stop("end_turn"), End);
        await Collect(Rewriter(h).StreamAsync(Req(mode, "ctx"), default));

        var body = Body(h);
        Assert.Equal(model, body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("max_tokens").GetInt32() > 0);
        Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
        var t = body.GetProperty("thinking");
        Assert.Equal(thinking, t.GetProperty("type").GetString());
        Assert.Single(t.EnumerateObject()); // between_tools accepts no other field
        Assert.Equal(JsonValueKind.String, body.GetProperty("system").ValueKind);
        var msg = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", msg.GetProperty("role").GetString());
        foreach (var banned in new[] { "temperature", "top_p", "top_k", "tools", "tool_choice" })
            Assert.False(body.TryGetProperty(banned, out _), banned);

        var sonnet = model == ModelIds.Sonnet;
        Assert.Equal(sonnet, body.TryGetProperty("fallbacks", out var fb));
        if (sonnet) Assert.Equal("default", fb.GetString());
        var req = h.Requests[0].Request;
        Assert.Equal(sonnet, req.Headers.TryGetValues("anthropic-beta", out var betas));
        if (sonnet) Assert.Equal(["server-side-fallback-2026-07-01"], betas);
    }

    [Fact]
    public async Task Headers_KeyPerRequest_VersionPinned_NoBearer_FixedUrl()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://evil.example/");
        try
        {
            var keys = new Keys(() => _key);
            var h = Ok(Start, Delta("x"), Stop("end_turn"), End);
            await Collect(Rewriter(h, keys).StreamAsync(Req(), default));
            var req = h.Requests[0].Request;
            Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), req.RequestUri);
            Assert.Equal(["test-key"], req.Headers.GetValues("x-api-key"));
            Assert.Equal(["2026-10-21"], req.Headers.GetValues("anthropic-version"));
            Assert.Null(req.Headers.Authorization);
            Assert.Equal(["YOLIME_ANTHROPIC_API_KEY"], keys.Asked.Distinct());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", previous);
        }
    }

    [Fact]
    public async Task NoKey_FailsBeforeAnyNetworkCall()
    {
        _key = null;
        var h = Ok(Start, End);
        var r = Rewriter(h);
        Assert.Equal(RewriterReadiness.MissingKey, r.CheckReady());
        var ex = await Assert.ThrowsAsync<RewriteException>(() => Collect(r.StreamAsync(Req(), default)));
        Assert.Equal(RewriteFailure.MissingKey, ex.Failure);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Ready_WithAKey() => Assert.Equal(RewriterReadiness.Ready, Rewriter(Ok()).CheckReady());

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, RewriteFailure.Config)]
    [InlineData(HttpStatusCode.NotFound, RewriteFailure.Config)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, RewriteFailure.Config)]
    [InlineData(HttpStatusCode.Unauthorized, RewriteFailure.Auth)]
    [InlineData(HttpStatusCode.Forbidden, RewriteFailure.Auth)]
    [InlineData(HttpStatusCode.PaymentRequired, RewriteFailure.Billing)]
    [InlineData(HttpStatusCode.TooManyRequests, RewriteFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, RewriteFailure.Server)]
    [InlineData((HttpStatusCode)529, RewriteFailure.Server)]
    public async Task Status_MapsToAFailure(HttpStatusCode status, RewriteFailure expected)
    {
        var ex = await Assert.ThrowsAsync<RewriteException>(() => Collect(Rewriter(Status(status, "0")).StreamAsync(Req(), default)));
        Assert.Equal(expected, ex.Failure);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("SENTINEL", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)529)]
    public async Task Overloaded_RetriesOnce_ThenSucceeds(HttpStatusCode first)
    {
        var calls = 0;
        var h = new FakeHttpHandler(_ => ++calls == 1
            ? new HttpResponseMessage(first) { Headers = { { "retry-after", "0" } }, Content = new StringContent("{}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse(Start, Delta("ok"), Stop("end_turn"), End)) });
        Assert.Equal("ok", await Collect(Rewriter(h).StreamAsync(Req(), default)));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task ServerError_IsNotRetried_AndASecondOverloadGivesUp()
    {
        var h500 = Status(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<RewriteException>(() => Collect(Rewriter(h500).StreamAsync(Req(), default)));
        Assert.Single(h500.Requests);

        var h429 = Status(HttpStatusCode.TooManyRequests, "0");
        await Assert.ThrowsAsync<RewriteException>(() => Collect(Rewriter(h429).StreamAsync(Req(), default)));
        Assert.Equal(2, h429.Requests.Count);
    }

    [Theory]
    [InlineData("refusal", RewriteFailure.Refusal)]
    [InlineData("max_tokens", RewriteFailure.MaxTokens)]
    [InlineData("pause_turn", RewriteFailure.Server)]
    [InlineData("tool_use", RewriteFailure.Server)]
    public async Task NonEndTurnStops_Fail_AfterTheDeltas(string reason, RewriteFailure expected)
    {
        var got = new StringBuilder();
        var ex = await Assert.ThrowsAsync<RewriteException>(async () =>
        {
            await foreach (var d in Rewriter(Ok(Start, Delta("partial"), Stop(reason, "cyber"), End)).StreamAsync(Req(), default)) got.Append(d);
        });
        Assert.Equal(expected, ex.Failure);
        Assert.Equal("partial", got.ToString());
    }

    [Fact]
    public async Task EmptyOutput_Fails() =>
        Assert.Equal(RewriteFailure.EmptyOutput, (await Assert.ThrowsAsync<RewriteException>(() =>
            Collect(Rewriter(Ok(Start, Stop("end_turn"), End)).StreamAsync(Req(), default)))).Failure);

    [Fact]
    public async Task EofBeforeMessageStop_IsNetwork() =>
        Assert.Equal(RewriteFailure.Network, (await Assert.ThrowsAsync<RewriteException>(() =>
            Collect(Rewriter(Ok(Start, Delta("cut"))).StreamAsync(Req(), default)))).Failure);

    [Theory]
    [InlineData("overloaded_error", RewriteFailure.Server)]
    [InlineData("rate_limit_error", RewriteFailure.RateLimited)]
    [InlineData("api_error", RewriteFailure.Server)]
    public async Task MidStreamErrorEvent_Maps(string type, RewriteFailure expected)
    {
        var error = """{"type":"error","error":{"type":""" + JsonSerializer.Serialize(type) + ""","message":"SENTINEL_BODY"}}""";
        var ex = await Assert.ThrowsAsync<RewriteException>(() => Collect(Rewriter(Ok(Start, Delta("a"), error)).StreamAsync(Req(), default)));
        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task NonTextBlocks_AreSkipped()
    {
        var h = Ok(Start,
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"SECRET"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"fallback","from":{"model":"a"},"to":{"model":"b"}}}""",
            """{"type":"some_future_event","x":1}""",
            Delta("text"), Stop("end_turn"), End);
        Assert.Equal("text", await Collect(Rewriter(h).StreamAsync(Req(), default)));
    }

    [Fact]
    public async Task Sse_ToleratesCrlf_NoSpaceAfterColon_AndSplitChunks()
    {
        var raw = $"event: a\r\ndata:{Start}\r\n\r\ndata: {Delta("one ")}\r\n\r\n: comment\n\ndata: {Delta("two")}\n\ndata: {Stop("end_turn")}\n\ndata: {End}\n\n";
        var h = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TrickleStream(Encoding.UTF8.GetBytes(raw), 7)) });
        Assert.Equal("one two", await Collect(Rewriter(h).StreamAsync(Req(), default)));
    }

    [Fact]
    public async Task NetworkError_IsNetwork_WithoutTheExceptionText()
    {
        var h = FakeHttpHandler.Throws(new HttpRequestException("SENTINEL at https://sentinel.example/"));
        var ex = await Assert.ThrowsAsync<RewriteException>(() => Collect(Rewriter(h).StreamAsync(Req(), default)));
        Assert.Equal(RewriteFailure.Network, ex.Failure);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task ATimeoutThatIsNotTheCallers_IsTimeout()
    {
        var h = FakeHttpHandler.Throws(new TaskCanceledException("connect timeout"));
        Assert.Equal(RewriteFailure.Timeout, (await Assert.ThrowsAsync<RewriteException>(() => Collect(Rewriter(h).StreamAsync(Req(), default)))).Failure);
    }

    [Fact]
    public async Task TheCallersCancel_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(Rewriter(Ok(Start, End)).StreamAsync(Req(), cts.Token)));
    }

    [Fact]
    public async Task Logs_NeverHoldContent()
    {
        var log = new Yolime.Core.Logging.FileLog(_root.Paths, TimeProvider.System);
        ClaudeRewriter With(HttpMessageHandler h) =>
            new(HttpClients.Create(h), new Keys(() => "SENTINEL_KEY"), new PromptBuilder(new PromptLibrary(_root.Paths)), () => _settings, log);
        var request = new RewriteRequest(Mode.Reply, "SENTINEL_TRANSCRIPT", new CapturedContext("SENTINEL_CTX", ContextSource.Selection, 0));

        await Collect(With(Ok(Start, Delta("SENTINEL_OUTPUT"), Stop("end_turn"), End)).StreamAsync(request, default));
        await Assert.ThrowsAsync<RewriteException>(() => Collect(With(Ok(Start, Delta("SENTINEL_OUTPUT"), Stop("refusal", "cyber"), End)).StreamAsync(request, default)));
        await Assert.ThrowsAsync<RewriteException>(() => Collect(With(Status(HttpStatusCode.BadRequest)).StreamAsync(request, default)));
        await Assert.ThrowsAsync<RewriteException>(() => Collect(With(FakeHttpHandler.Throws(new HttpRequestException("SENTINEL_EXCEPTION"))).StreamAsync(request, default)));
        await Assert.ThrowsAsync<RewriteException>(() => Collect(With(Ok(Start, """{"type":"error","error":{"type":"overloaded_error","message":"SENTINEL_BODY"}}""")).StreamAsync(request, default)));

        var files = Directory.GetFiles(_root.Paths.LogsDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.DoesNotContain("SENTINEL", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns at most <paramref name="chunk"/> bytes per read, splitting lines and UTF-8 sequences.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
    }
}
