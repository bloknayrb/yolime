using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Yolime.Core.Llm;
using Yolime.Core.Modes;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Keys;

public enum KeyTestResult { Ok, Rejected, Unreachable, NotSet, BadEndpoint, Malformed }

/// <summary>
/// Checks a key with one request that costs nothing: list models (OpenAI, Groq, the custom endpoint) or count tokens
/// (Claude). Hosts are fixed per slot, so a key is only ever sent to its own provider. Bodies are never read.
/// </summary>
public sealed class KeyTester(HttpClient http, IApiKeySource keys, Func<YolimeSettings> settings)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri OpenAiModels = new("https://api.openai.com/v1/models");
    private static readonly Uri GroqModels = new("https://api.groq.com/openai/v1/models");
    private static readonly Uri ClaudeCountTokens = new("https://api.anthropic.com/v1/messages/count_tokens");

    /// <param name="candidate">A key typed but not yet saved; when empty, the key in use is tested.</param>
    public async Task<KeyTestResult> TestAsync(string slot, string? candidate, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(candidate) ? keys.Get(slot) : candidate.Trim();
        if (string.IsNullOrEmpty(key)) return KeyTestResult.NotSet;
        // A typed key isn't validated by Save yet; a newline or non-ASCII character would throw from the header.
        if (!KeySource.IsWellFormed(key)) return KeyTestResult.Malformed;

        HttpRequestMessage request;
        switch (slot)
        {
            case SttEndpoint.OpenAiKeyName:
                request = Bearer(OpenAiModels, key);
                break;
            case SttEndpoint.GroqKeyName:
                request = Bearer(GroqModels, key);
                break;
            case SttEndpoint.CustomKeyName:
                if (SttEndpoint.CheckCustomUrl(settings().CustomSttBaseUrl, out var custom) != SttReadiness.Ready)
                    return KeyTestResult.BadEndpoint;
                request = Bearer(new Uri(custom!, "models"), key);
                break;
            case ClaudeRewriter.KeyName:
                request = new HttpRequestMessage(HttpMethod.Post, ClaudeCountTokens)
                {
                    Content = new StringContent(
                        "{\"model\":\"" + ModelIds.Haiku + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                        Encoding.UTF8,
                        "application/json"),
                };
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", ClaudeRewriter.ApiVersion);
                break;
            default:
                return KeyTestResult.NotSet;
        }

        // The shared client refuses any request that doesn't name its host: the fixed host for this slot.
        Net.HostPinningHandler.Expect(request, request.RequestUri!.Host);
        using (request)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(Timeout);
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                return response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => KeyTestResult.Rejected,
                    // A rate limit is applied only after the key has been accepted.
                    HttpStatusCode.TooManyRequests => KeyTestResult.Ok,
                    _ when response.IsSuccessStatusCode => KeyTestResult.Ok,
                    _ => KeyTestResult.Unreachable,
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                return KeyTestResult.Unreachable;
            }
        }
    }

    private static HttpRequestMessage Bearer(Uri uri, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
