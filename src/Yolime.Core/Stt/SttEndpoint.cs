using System.Diagnostics.CodeAnalysis;
using Yolime.Core.Settings;

namespace Yolime.Core.Stt;

/// <summary>
/// Where to send audio and with which key. Each provider's key is bound to that provider's fixed host, and a custom
/// endpoint has its own key, so a mistyped base URL can never receive the OpenAI or Groq key.
/// </summary>
public sealed record SttEndpoint(Uri BaseUrl, string Model, string ApiKey, SttProviderKind Kind)
{
    public const string OpenAiKeyName = "OPENAI_API_KEY";
    public const string GroqKeyName = "GROQ_API_KEY";
    public const string CustomKeyName = "YOLIME_STT_API_KEY";

    private static readonly Uri OpenAiBase = new("https://api.openai.com/v1/");
    private static readonly Uri GroqBase = new("https://api.groq.com/openai/v1/");

    public static SttEndpoint Resolve(YolimeSettings settings, IApiKeySource keys) =>
        TryResolve(settings, keys, out var ep, out var readiness)
            ? ep
            : throw new SttException(readiness switch
            {
                SttReadiness.MissingKey => SttError.MissingKey,
                SttReadiness.InsecureEndpoint => SttError.InsecureEndpoint,
                _ => SttError.InvalidEndpoint,
            });

    public static bool TryResolve(YolimeSettings settings, IApiKeySource keys, [NotNullWhen(true)] out SttEndpoint? endpoint, out SttReadiness readiness)
    {
        endpoint = null;
        Uri baseUrl;
        string model, keyName;
        switch (settings.SttProvider)
        {
            case SttProviderKind.OpenAi:
                (baseUrl, model, keyName) = (OpenAiBase, settings.OpenAiSttModel, OpenAiKeyName);
                break;
            case SttProviderKind.Groq:
                (baseUrl, model, keyName) = (GroqBase, settings.GroqSttModel, GroqKeyName);
                break;
            default:
                readiness = CheckCustomUrl(settings.CustomSttBaseUrl, out var custom);
                if (readiness != SttReadiness.Ready || string.IsNullOrWhiteSpace(settings.CustomSttModel))
                {
                    if (readiness == SttReadiness.Ready) readiness = SttReadiness.InvalidEndpoint;
                    return false;
                }
                (baseUrl, model, keyName) = (custom!, settings.CustomSttModel, CustomKeyName);
                break;
        }

        if (keys.Get(keyName) is not { Length: > 0 } key)
        {
            readiness = SttReadiness.MissingKey;
            return false;
        }
        endpoint = new SttEndpoint(baseUrl, model, key, settings.SttProvider);
        readiness = SttReadiness.Ready;
        return true;
    }

    /// <summary>The provider's base URL with no key attached (connection warm-up). Null for an invalid custom endpoint.</summary>
    public static Uri? BaseFor(YolimeSettings settings) => settings.SttProvider switch
    {
        SttProviderKind.OpenAi => OpenAiBase,
        SttProviderKind.Groq => GroqBase,
        _ => CheckCustomUrl(settings.CustomSttBaseUrl, out var url) == SttReadiness.Ready ? url : null,
    };

    internal static SttReadiness CheckCustomUrl(string? text, out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return SttReadiness.InvalidEndpoint;
        if (parsed.Scheme == Uri.UriSchemeHttp) return SttReadiness.InsecureEndpoint;
        // Credentials, query strings and fragments in the URL would end up in settings.json and in exception text.
        if (parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
            return SttReadiness.InvalidEndpoint;
        // Without a trailing slash, new Uri(base, "audio/transcriptions") would drop the last path segment.
        url = parsed.AbsolutePath.EndsWith('/') ? parsed : new Uri(parsed.GetLeftPart(UriPartial.Path) + "/");
        return SttReadiness.Ready;
    }
}
