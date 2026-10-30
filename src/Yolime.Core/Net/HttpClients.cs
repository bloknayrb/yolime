namespace Yolime.Core.Net;

/// <summary>
/// The one HTTP client yolime uses. Redirects are off (a redirect would re-send the user's audio to another host),
/// there are no default auth headers (keys go on each request, so one provider's key can't reach another), and
/// every request must name the host it expects (<see cref="HostPinningHandler"/>).
/// </summary>
public static class HttpClients
{
    public static HttpClient Shared { get; } = Create(CreateSocketsHandler());

    private static readonly HttpRequestOptionsKey<bool> OpenedConnection = new("yolime.openedConnection");

    /// <summary>
    /// Whether this request triggered a connection attempt. Roughly "it wasn't warm": the pool can serve a request on a
    /// different connection than the one it triggered, so read it over many runs, not one.
    /// </summary>
    public static bool OpenedNewConnection(HttpRequestMessage request) =>
        request.Options.TryGetValue(OpenedConnection, out var opened) && opened;

    internal static SocketsHttpHandler CreateSocketsHandler() => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            // Per request, not a process-wide counter: a warm-up opening a connection at the same moment as a real
            // request mustn't make that request look cold.
            context.InitialRequestMessage.Options.Set(OpenedConnection, true);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Wraps <paramref name="inner"/> with host pinning. Timeouts come from callers' tokens.</summary>
    internal static HttpClient Create(HttpMessageHandler inner) =>
        new(new HostPinningHandler { InnerHandler = inner }) { Timeout = Timeout.InfiniteTimeSpan };
}

public sealed class HostPinningException() : Exception("request host does not match its endpoint");

/// <summary>
/// Refuses any request that doesn't carry the host it was built for, or whose URI points elsewhere. A defence in
/// depth for the key-to-host binding: a request can only leave the process if the caller said where it should go.
/// </summary>
public sealed class HostPinningHandler : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<string> ExpectedHost = new("yolime.expectedHost");

    public static void Expect(HttpRequestMessage request, string host) => request.Options.Set(ExpectedHost, host);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // https only: a key must never travel in clear text, whatever built the request.
        if (!request.Options.TryGetValue(ExpectedHost, out var host)
            || request.RequestUri is null
            || request.RequestUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(request.RequestUri.Host, host, StringComparison.OrdinalIgnoreCase))
            throw new HostPinningException();
        return SendAndReportAsync(request, host, ct);
    }

    /// <summary>A host whose request just finished (its connection is warm). The warmer listens.</summary>
    public static event Action<string>? RequestCompleted;

    private async Task<HttpResponseMessage> SendAndReportAsync(HttpRequestMessage request, string host, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        try { RequestCompleted?.Invoke(host); } catch (Exception) { } // a listener's bug mustn't fail the request
        return response;
    }
}
