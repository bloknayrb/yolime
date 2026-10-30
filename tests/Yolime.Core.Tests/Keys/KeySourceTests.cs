using System.Net;
using Yolime.Core.Keys;
using Yolime.Core.Llm;
using Yolime.Core.Settings;
using Yolime.Core.Stt;
using Yolime.Core.Tests.Stt;

namespace Yolime.Core.Tests.Keys;

public sealed class FakeCredentialStore : ICredentialStore
{
    public Dictionary<string, string> Saved { get; } = new(StringComparer.Ordinal);
    public bool FailWrites { get; set; }

    public string? Read(string name) => Saved.GetValueOrDefault(name);

    public bool Write(string name, string secret)
    {
        if (FailWrites) return false;
        Saved[name] = secret;
        return true;
    }

    public bool Delete(string name) => Saved.Remove(name) || true;
}

public class KeySourceTests
{
    private const string Long = "sk-proj-abcdefghijklmnopqrstuvwxyz1234";

    private static (KeySource Keys, FakeCredentialStore Store, Dictionary<string, string> Env) Make()
    {
        var store = new FakeCredentialStore();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        return (new KeySource(store, n => env.GetValueOrDefault(n)), store, env);
    }

    [Fact]
    public void ASavedKey_WinsOverTheEnvironment()
    {
        var (keys, store, env) = Make();
        env[SttEndpoint.OpenAiKeyName] = "env-key";
        Assert.Equal("env-key", keys.Get(SttEndpoint.OpenAiKeyName));
        Assert.Equal(KeyOrigin.Environment, keys.Status(SttEndpoint.OpenAiKeyName).Origin);

        Assert.Equal(KeySaveError.None, keys.Save(SttEndpoint.OpenAiKeyName, "  " + Long + "\r\n"));
        Assert.Equal(Long, store.Saved[SttEndpoint.OpenAiKeyName]); // trimmed
        Assert.Equal(Long, keys.Get(SttEndpoint.OpenAiKeyName));
        Assert.Equal(KeyOrigin.Saved, keys.Status(SttEndpoint.OpenAiKeyName).Origin);

        keys.Remove(SttEndpoint.OpenAiKeyName);
        Assert.Equal("env-key", keys.Get(SttEndpoint.OpenAiKeyName));
    }

    [Fact]
    public void OnlyTheFourSlots_AreRead()
    {
        var (keys, store, env) = Make();
        env["ANTHROPIC_API_KEY"] = Long; // deliberately ignored: it may belong to another tool's budget
        store.Saved["ANTHROPIC_API_KEY"] = Long;
        Assert.Null(keys.Get("ANTHROPIC_API_KEY"));
        Assert.Equal(KeySaveError.UnknownSlot, keys.Save("ANTHROPIC_API_KEY", Long));
        Assert.Equal(
            [SttEndpoint.OpenAiKeyName, SttEndpoint.GroqKeyName, SttEndpoint.CustomKeyName, ClaudeRewriter.KeyName],
            KeySource.Slots);
    }

    [Theory]
    [InlineData("", KeySaveError.Empty)]
    [InlineData("   ", KeySaveError.Empty)]
    [InlineData("sk-abc def", KeySaveError.BadCharacters)]
    [InlineData("sk-ab\u00e9c", KeySaveError.BadCharacters)]
    [InlineData("sk-ab\u200bc", KeySaveError.BadCharacters)]
    public void BadKeys_AreRefusedBeforeSaving(string raw, KeySaveError expected)
    {
        var (keys, store, _) = Make();
        Assert.Equal(expected, keys.Save(ClaudeRewriter.KeyName, raw));
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void AnOverlongKey_IsRefused()
    {
        var (keys, _, _) = Make();
        Assert.Equal(KeySaveError.TooLong, keys.Save(ClaudeRewriter.KeyName, new string('a', 2561)));
        Assert.Equal(KeySaveError.None, keys.Save(ClaudeRewriter.KeyName, new string('a', 2560)));
    }

    [Fact]
    public void AStoreFailure_IsReported()
    {
        var (keys, store, _) = Make();
        store.FailWrites = true;
        Assert.Equal(KeySaveError.StoreFailed, keys.Save(ClaudeRewriter.KeyName, Long));
    }

    [Fact]
    public void TheLastFour_AreShownOnlyForLongKeys()
    {
        var (keys, _, env) = Make();
        keys.Save(ClaudeRewriter.KeyName, Long);
        Assert.Equal("1234", keys.Status(ClaudeRewriter.KeyName).LastFour);
        env[SttEndpoint.GroqKeyName] = "short-key-1234567"; // 17 characters: the tail would give too much away
        Assert.Null(keys.Status(SttEndpoint.GroqKeyName).LastFour);
        Assert.Equal(new KeyStatus(KeyOrigin.None, null), keys.Status(SttEndpoint.CustomKeyName));
    }

    [Fact]
    public void Changes_AreAnnounced()
    {
        var (keys, _, _) = Make();
        var changed = new List<string>();
        keys.Changed += changed.Add;
        keys.Save(ClaudeRewriter.KeyName, Long);
        keys.Remove(ClaudeRewriter.KeyName);
        keys.Save(ClaudeRewriter.KeyName, "");
        Assert.Equal([ClaudeRewriter.KeyName, ClaudeRewriter.KeyName], changed);
    }
}

public class KeyTesterTests
{
    private const string Key = "sk-test-abcdefghijklmnopqrstuvwxyz";

    private static async Task<(KeyTestResult Result, FakeHttpHandler Handler)> Run(
        string slot, FakeHttpHandler handler, YolimeSettings? settings = null, string? candidate = Key)
    {
        var tester = new KeyTester(Yolime.Core.Net.HttpClients.Create(handler), // the production wrapper: pinning included
             new KeySource(new FakeCredentialStore(), _ => null), () => settings ?? new YolimeSettings());
        return (await tester.TestAsync(slot, candidate, TestContext.Current.CancellationToken), handler);
    }

    [Fact]
    public async Task OpenAi_ListsModels_OnItsOwnHost()
    {
        var (result, h) = await Run(SttEndpoint.OpenAiKeyName, FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        Assert.Equal(KeyTestResult.Ok, result);
        var req = Assert.Single(h.Requests).Request;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.openai.com/v1/models", req.RequestUri!.ToString());
        Assert.Equal("Bearer " + Key, req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Groq_ListsModels_OnItsOwnHost()
    {
        var (_, h) = await Run(SttEndpoint.GroqKeyName, FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        Assert.Equal("https://api.groq.com/openai/v1/models", Assert.Single(h.Requests).Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Claude_CountsTokens_AndNothingElse()
    {
        var (result, h) = await Run(ClaudeRewriter.KeyName, FakeHttpHandler.Json(HttpStatusCode.OK, "{\"input_tokens\":3}"));
        Assert.Equal(KeyTestResult.Ok, result);
        var (req, body) = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages/count_tokens", req.RequestUri!.ToString());
        Assert.Equal(Key, req.Headers.GetValues("x-api-key").Single());
        Assert.Equal(ClaudeRewriter.ApiVersion, req.Headers.GetValues("anthropic-version").Single());
        Assert.Contains("claude-haiku-5-5", System.Text.Encoding.UTF8.GetString(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCustomEndpoint_IsTestedOnlyOverHttps()
    {
        var https = new YolimeSettings { CustomSttBaseUrl = "https://stt.example.com/v1", CustomSttModel = "m" };
        var (ok, h) = await Run(SttEndpoint.CustomKeyName, FakeHttpHandler.Json(HttpStatusCode.OK, "{}"), https);
        Assert.Equal(KeyTestResult.Ok, ok);
        Assert.Equal("https://stt.example.com/v1/models", Assert.Single(h.Requests).Request.RequestUri!.ToString());

        var http = https with { CustomSttBaseUrl = "http://stt.example.com/v1" };
        var (refused, h2) = await Run(SttEndpoint.CustomKeyName, FakeHttpHandler.Json(HttpStatusCode.OK, "{}"), http);
        Assert.Equal(KeyTestResult.BadEndpoint, refused);
        Assert.Empty(h2.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, KeyTestResult.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, KeyTestResult.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, KeyTestResult.Ok)] // rate limits apply only after the key is accepted
    [InlineData(HttpStatusCode.InternalServerError, KeyTestResult.Unreachable)]
    [InlineData(HttpStatusCode.NotFound, KeyTestResult.Unreachable)]
    public async Task Statuses_MapToAResult(HttpStatusCode status, KeyTestResult expected)
    {
        var (result, _) = await Run(SttEndpoint.OpenAiKeyName, FakeHttpHandler.Json(status, "{\"error\":\"sk-secret\"}"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ANetworkFailure_IsUnreachable()
    {
        var (result, _) = await Run(SttEndpoint.OpenAiKeyName, FakeHttpHandler.Throws(new HttpRequestException("no route")));
        Assert.Equal(KeyTestResult.Unreachable, result);
    }

    [Theory]
    [InlineData("sk-abc\ndef")]
    [InlineData("sk-ab\u00e9c")]
    public async Task AMalformedTypedKey_IsNotSent(string candidate)
    {
        // A newline or a non-ASCII character would throw from the header, and would never be a valid key anyway.
        var (result, h) = await Run(SttEndpoint.OpenAiKeyName, FakeHttpHandler.Json(HttpStatusCode.OK, "{}"), candidate: candidate);
        Assert.Equal(KeyTestResult.Malformed, result);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task WithNothingTypedOrSaved_NothingIsSent()
    {
        var (result, h) = await Run(SttEndpoint.OpenAiKeyName, FakeHttpHandler.Json(HttpStatusCode.OK, "{}"), candidate: null);
        Assert.Equal(KeyTestResult.NotSet, result);
        Assert.Empty(h.Requests);
    }
}
