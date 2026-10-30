using System.Collections.Concurrent;
using Yolime.Core.Logging;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Net;

/// <summary>
/// Opens the TLS connections a hold will need while the user is still speaking, so key-up doesn't pay for a handshake.
/// At key-down, each host (the transcription provider's and Claude's, for every mode: Dictate calls Claude for longer
/// text too) gets a keyless HEAD if it hasn't been used for <see cref="IdleAfter"/>. The answer (usually 401) doesn't
/// matter: the connection stays in the pool for the real request.
/// </summary>
/// <remarks>
/// Built from bare base URLs, never from an endpoint record that carries a key, so a warm-up can't send one. Pinned
/// like every request. Best effort: any failure is ignored, and the real request simply opens its own connection.
/// </remarks>
/// <param name="wanted">Which bases to warm: those with a key configured. Null warms both.</param>
public sealed class ConnectionWarmer(HttpClient http, TimeProvider time, ILog log, Func<Uri, bool>? wanted = null)
{
    public static Uri ClaudeHost => ClaudeBase;

    /// <summary>Servers drop idle connections on their own schedule; 30 s is under any we've seen. Tune with NewConnection.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(30);

    private static readonly Uri ClaudeBase = new("https://api.anthropic.com/");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, long> _lastUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A real request to this host just finished, so its connection is warm.</summary>
    public void MarkUsed(string host) => _lastUse[host] = time.GetTimestamp();

    /// <summary>Warms each host that's gone idle. Returns when they're done (callers fire and forget).</summary>
    public async Task WarmAsync(YolimeSettings settings)
    {
        var bases = new List<Uri> { ClaudeBase };
        // A streamed upload opens its connection while the user speaks, so its handshake is already hidden.
        if (!SttStreaming.Applies(settings) && SttEndpoint.BaseFor(settings) is { } stt) bases.Add(stt);
        // No traffic to a provider the user hasn't set up (no Claude key yet, say).
        await Task.WhenAll(bases.Where(b => wanted?.Invoke(b) ?? true).Select(WarmOneAsync)).ConfigureAwait(false);
    }

    private async Task WarmOneAsync(Uri baseUri)
    {
        var host = baseUri.Host;
        if (_lastUse.TryGetValue(host, out var last) && time.GetElapsedTime(last) < IdleAfter) return;
        // One warm-up per host at a time: a second would only open a second connection.
        if (!_inFlight.TryAdd(host, 0)) return;
        var start = time.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, baseUri);
            HostPinningHandler.Expect(request, host);
            using var cts = new CancellationTokenSource(Timeout);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            MarkUsed(host);
            log.Write(LogEvent.ConnectionWarmed, LogField.Ms("ms", (long)time.GetElapsedTime(start).TotalMilliseconds));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or HostPinningException)
        {
            // Best effort: the real request opens its own connection.
        }
        finally
        {
            _inFlight.TryRemove(host, out _);
        }
    }
}
