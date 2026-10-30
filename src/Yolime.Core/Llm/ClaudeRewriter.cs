using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Yolime.Core.Logging;
using Yolime.Core.Modes;
using Yolime.Core.Net;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Llm;

/// <summary>Why a refusal happened, as the API reports it. Logged as a tag; the server's string never is.</summary>
public enum StopCategory { None, Cyber, Bio, ReasoningExtraction, FrontierLlm, Unknown }

/// <summary>
/// Streams a rewrite from the Claude Messages API over plain <see cref="HttpClient"/>. Requests go only to the fixed
/// Anthropic host, carry the key on the request itself, and never send sampling parameters or tools.
/// </summary>
/// <remarks>
/// Every failure becomes a <see cref="RewriteException"/> without an inner exception or response text: HTTP and JSON
/// exceptions quote URLs and bodies, which could hold the user's words. Only the caller's own cancellation
/// propagates as <see cref="OperationCanceledException"/>.
/// </remarks>
public sealed class ClaudeRewriter(HttpClient http, IApiKeySource keys, PromptBuilder prompts, Func<YolimeSettings> settings, ILog log, TimeProvider? time = null) : IRewriter
{
    /// <summary>Not <c>ANTHROPIC_API_KEY</c>: Claude Code honours that one, and setting it could change how Claude Code bills.</summary>
    public const string KeyName = "YOLIME_ANTHROPIC_API_KEY";

    public const string ApiVersion = "2026-10-21";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const string Host = "api.anthropic.com";
    private static readonly Uri Endpoint = new($"https://{Host}/v1/messages");
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public RewriterReadiness CheckReady() => keys.Get(KeyName) is null ? RewriterReadiness.MissingKey : RewriterReadiness.Ready;

    public async IAsyncEnumerable<string> StreamAsync(RewriteRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // C# can't yield inside a try that has a catch, so the parse runs in an inner iterator and its failures are
        // translated here, one step at a time.
        await using var inner = ReadAsync(request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            string delta;
            try
            {
                if (!await inner.MoveNextAsync()) break;
                delta = inner.Current;
            }
            catch (RewriteException ex)
            {
                log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Rewrite), LogField.Tag("rewriteError", ex.Failure));
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failure = ex switch
                {
                    OperationCanceledException => RewriteFailure.Timeout, // the handler's connect timeout, not the caller
                    HttpRequestException or IOException or HostPinningException => RewriteFailure.Network,
                    _ => RewriteFailure.Server, // JsonException and anything unexpected in the stream
                };
                log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Rewrite), LogField.Tag("rewriteError", failure), LogField.Error(ex));
                throw new RewriteException(failure);
            }
            yield return delta;
        }
    }

    private async IAsyncEnumerable<string> ReadAsync(RewriteRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var def = ModeCatalog.For(request.Mode, settings()) ?? throw new RewriteException(RewriteFailure.Config);
        var key = keys.Get(KeyName) ?? throw new RewriteException(RewriteFailure.MissingKey);
        var body = BuildBody(def, prompts.Build(request, settings().Vocabulary));

        using var response = await SendWithRetryAsync(def, key, body, ct);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode) throw new RewriteException(MapStatus(status));

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        string? stopReason = null;
        var category = StopCategory.None;
        var chars = 0;
        var finished = false;

        while (!finished && await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 0)
            {
                // "data:" with or without a space; "event:", "id:" and ":" comments carry nothing we need.
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                }
                continue;
            }
            if (data.Length == 0) continue;

            using var doc = JsonDocument.Parse(data.ToString());
            data.Clear();
            var evt = doc.RootElement;
            switch (evt.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "content_block_delta":
                    if (evt.TryGetProperty("delta", out var d) && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                        && d.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } s)
                    {
                        chars += s.Length;
                        yield return s;
                    }
                    break;
                case "message_delta":
                    if (evt.TryGetProperty("delta", out var md))
                    {
                        if (md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String) stopReason = sr.GetString();
                        if (md.TryGetProperty("stop_details", out var sd) && sd.ValueKind == JsonValueKind.Object
                            && sd.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String)
                            category = Category(c.GetString());
                    }
                    break;
                case "message_stop":
                    finished = true;
                    break;
                case "error":
                    var type = evt.TryGetProperty("error", out var e) && e.TryGetProperty("type", out var et) ? et.GetString() : null;
                    throw new RewriteException(type == "rate_limit_error" ? RewriteFailure.RateLimited : RewriteFailure.Server);
                // message_start, content_block_start/stop, ping and future event types carry no text.
            }
        }

        if (!finished) throw new RewriteException(RewriteFailure.Network); // the stream ended mid-message
        var outcome = stopReason switch
        {
            "end_turn" when chars == 0 => RewriteFailure.EmptyOutput,
            "end_turn" => (RewriteFailure?)null,
            "refusal" => RewriteFailure.Refusal,
            "max_tokens" => RewriteFailure.MaxTokens,
            _ => RewriteFailure.Server,
        };
        if (outcome is { } failure)
        {
            if (category != StopCategory.None) log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Rewrite), LogField.Tag("stopCategory", category));
            throw new RewriteException(failure);
        }
        log.Write(LogEvent.StageCompleted, LogField.Tag("stage", Stage.Rewrite), LogField.HttpStatus(status), LogField.Count("outputChars", chars));
    }

    /// <summary>One retry on 429 or 529 (overloaded). It can only happen before any text, since it's decided on the status.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(ModeDefinition def, string key, byte[] body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", ApiVersion);
            if (def.Model == ModelIds.Sonnet) request.Headers.Add("anthropic-beta", FallbackBeta);
            HostPinningHandler.Expect(request, Host);

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (attempt > 0 || status is not (429 or 529)) return response;

            var wait = RetryWait(response);
            response.Dispose();
            log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Rewrite), LogField.HttpStatus(status), LogField.Ms("retryInMs", (long)wait.TotalMilliseconds));
            await Task.Delay(wait, _time, ct);
        }
    }

    private static TimeSpan RetryWait(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after", out var values)
            && double.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryWait.TotalSeconds));
        return TimeSpan.FromSeconds(1);
    }

    private static byte[] BuildBody(ModeDefinition def, BuiltPrompt prompt)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", def.Model);
            w.WriteNumber("max_tokens", def.MaxTokens);
            w.WriteBoolean("stream", true);
            w.WriteString("system", prompt.System);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", prompt.User);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("output_config");
            w.WriteString("effort", def.Effort.ToString().ToLowerInvariant());
            w.WriteEndObject();
            switch (def.Thinking)
            {
                case Thinking.Disabled:
                    w.WriteStartObject("thinking");
                    w.WriteString("type", "disabled");
                    w.WriteEndObject();
                    break;
                case Thinking.BetweenTools:
                    w.WriteStartObject("thinking");
                    w.WriteString("type", "between_tools");
                    w.WriteEndObject();
                    break;
            }
            // Sonnet can decline on a safety classifier; "default" re-runs the request on Anthropic's recommended
            // fallback model server-side. Haiku has no server-side fallback.
            if (def.Model == ModelIds.Sonnet) w.WriteString("fallbacks", "default");
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static RewriteFailure MapStatus(int status) => status switch
    {
        400 or 404 or 413 => RewriteFailure.Config,
        401 or 403 => RewriteFailure.Auth,
        402 => RewriteFailure.Billing,
        429 => RewriteFailure.RateLimited,
        _ => RewriteFailure.Server,
    };

    private static StopCategory Category(string? value) => value switch
    {
        "cyber" => StopCategory.Cyber,
        "bio" => StopCategory.Bio,
        "reasoning_extraction" => StopCategory.ReasoningExtraction,
        "frontier_llm" => StopCategory.FrontierLlm,
        _ => StopCategory.Unknown,
    };
}
