using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Audio;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Stt;

/// <summary>
/// What actually reaches the wire, through the real <see cref="SocketsHttpHandler"/> and a loopback server: a disposed
/// stream must be torn down without the chunked terminator, so the server never has a complete request.
/// </summary>
public sealed class StreamingWireTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly MemoryStream _received = new();
    private readonly TaskCompletionSource _gotBody = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;

    public StreamingWireTests()
    {
        _listener.Start();
        _server = Task.Run(ServeOnceAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Reads one connection's bytes until the client closes it; never answers.</summary>
    private async Task ServeOnceAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                lock (_received) _received.Write(buffer, 0, n);
                if (Text().Contains("RIFF", StringComparison.Ordinal) && _received.Length > 20_000) _gotBody.TrySetResult();
            }
        }
        catch (IOException) { }
        _closed.TrySetResult();
    }

    private string Text()
    {
        lock (_received) return Encoding.Latin1.GetString(_received.ToArray());
    }

    private static byte[] Tone(int ms)
    {
        var n = ms * AudioFormat.SampleRate / 1000;
        var b = new byte[n * 2];
        for (var i = 0; i < n; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 440 * i / AudioFormat.SampleRate) * 0.2 * 32767));
        return b;
    }

    [Fact]
    public async Task ADisposedStream_IsTornDown_WithoutTheChunkedTerminator()
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var endpoint = new SttEndpoint(new Uri($"http://127.0.0.1:{Port}/v1/"), "gpt-4o-transcribe", "sk-test", SttProviderKind.OpenAi);
        var session = new StreamingTranscriptionSession(http, endpoint, new SttSessionOptions([], TrimBelow: 0.0025f, Live: true), new RecordingLog(), new FakeTimeProvider());
        var pcm = Tone(1500);
        for (var o = 0; o < pcm.Length; o += 3200) session.PushAudio(pcm.AsMemory(o, Math.Min(3200, pcm.Length - o)));

        await _gotBody.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Transfer-Encoding: chunked", Text(), StringComparison.OrdinalIgnoreCase);
        await session.DisposeAsync();
        await _closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var wire = Text();
        Assert.DoesNotContain("\r\n0\r\n\r\n", wire); // the last-chunk marker that ends a chunked body
        Assert.DoesNotContain("--\r\n", wire[^200..]); // nor the multipart's closing boundary
    }

    [Fact]
    public async Task ACompletedStream_DoesEndItsBody_SoTheCheckAboveCanFail()
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var endpoint = new SttEndpoint(new Uri($"http://127.0.0.1:{Port}/v1/"), "gpt-4o-transcribe", "sk-test", SttProviderKind.OpenAi);
        await using var session = new StreamingTranscriptionSession(http, endpoint, new SttSessionOptions([], TrimBelow: 0.0025f, Live: true), new RecordingLog(), new FakeTimeProvider());
        var pcm = Tone(1500);
        for (var o = 0; o < pcm.Length; o += 3200) session.PushAudio(pcm.AsMemory(o, Math.Min(3200, pcm.Length - o)));
        await _gotBody.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The server never answers: complete, wait for the terminator to arrive, then give up on the response.
        using var cts = new CancellationTokenSource();
        var complete = session.CompleteAsync(cts.Token);
        for (var i = 0; i < 500 && !Text().Contains("\r\n0\r\n\r\n", StringComparison.Ordinal); i++) await Task.Delay(10);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => complete);
        Assert.Contains("\r\n0\r\n\r\n", Text());
    }
}
