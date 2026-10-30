using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Stt;

public class SttEndpointTests
{
    private sealed class Keys(Dictionary<string, string> values) : IApiKeySource
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }

    private static readonly Keys AllKeys = new(new()
    {
        ["OPENAI_API_KEY"] = "sk-openai",
        ["GROQ_API_KEY"] = "gsk-groq",
        ["YOLIME_STT_API_KEY"] = "custom-key",
    });

    [Fact]
    public void Resolve_OpenAi_FixedHostAndKey()
    {
        var ep = SttEndpoint.Resolve(new YolimeSettings(), AllKeys);
        Assert.Equal("https://api.openai.com/v1/", ep.BaseUrl.ToString());
        Assert.Equal("sk-openai", ep.ApiKey);
        Assert.Equal("gpt-4o-transcribe", ep.Model);
    }

    [Fact]
    public void Resolve_Groq_DoesNotUseOpenAiKey()
    {
        var settings = new YolimeSettings { SttProvider = SttProviderKind.Groq };
        var onlyOpenAi = new Keys(new() { ["OPENAI_API_KEY"] = "sk-openai" });
        Assert.False(SttEndpoint.TryResolve(settings, onlyOpenAi, out _, out var readiness));
        Assert.Equal(SttReadiness.MissingKey, readiness);

        var ep = SttEndpoint.Resolve(settings, AllKeys);
        Assert.Equal("https://api.groq.com/openai/v1/", ep.BaseUrl.ToString());
        Assert.Equal("gsk-groq", ep.ApiKey);
    }

    [Fact]
    public void Resolve_Custom_DoesNotUseOpenAiKey()
    {
        var settings = new YolimeSettings { SttProvider = SttProviderKind.Custom, CustomSttBaseUrl = "https://stt.example/v1", CustomSttModel = "m" };
        var onlyOpenAi = new Keys(new() { ["OPENAI_API_KEY"] = "sk-openai" });
        Assert.False(SttEndpoint.TryResolve(settings, onlyOpenAi, out _, out var readiness));
        Assert.Equal(SttReadiness.MissingKey, readiness);
    }

    [Theory]
    [InlineData("http://stt.example/v1", SttReadiness.InsecureEndpoint)]
    [InlineData("https://user:pass@stt.example/v1", SttReadiness.InvalidEndpoint)]
    [InlineData("https://stt.example/v1?token=x", SttReadiness.InvalidEndpoint)]
    [InlineData("https://stt.example/v1#frag", SttReadiness.InvalidEndpoint)]
    [InlineData("/v1", SttReadiness.InvalidEndpoint)]
    [InlineData(null, SttReadiness.InvalidEndpoint)]
    public void Resolve_Custom_RejectsHttp_UserInfo_Query_Fragment_Relative(string? url, SttReadiness expected)
    {
        var settings = new YolimeSettings { SttProvider = SttProviderKind.Custom, CustomSttBaseUrl = url, CustomSttModel = "m" };
        Assert.False(SttEndpoint.TryResolve(settings, AllKeys, out _, out var readiness));
        Assert.Equal(expected, readiness);
        var ex = Assert.Throws<SttException>(() => SttEndpoint.Resolve(settings, AllKeys));
        Assert.Equal((SttError)Enum.Parse(typeof(SttError), expected.ToString()), ex.Error);
    }

    [Fact]
    public void Resolve_Custom_NormalisesTrailingSlash()
    {
        var settings = new YolimeSettings { SttProvider = SttProviderKind.Custom, CustomSttBaseUrl = "https://stt.example/api/v1", CustomSttModel = "m" };
        var ep = SttEndpoint.Resolve(settings, AllKeys);
        Assert.Equal("https://stt.example/api/v1/audio/transcriptions", new Uri(ep.BaseUrl, "audio/transcriptions").ToString());
    }

    [Fact]
    public void Resolve_MissingKey_TryResolveReportsMissingKey()
    {
        Assert.False(SttEndpoint.TryResolve(new YolimeSettings(), new Keys([]), out var ep, out var readiness));
        Assert.Null(ep);
        Assert.Equal(SttReadiness.MissingKey, readiness);
    }
}
