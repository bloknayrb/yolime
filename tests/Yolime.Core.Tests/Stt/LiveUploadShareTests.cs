using System.Net.Sockets;
using Yolime.Core.Audio;
using Yolime.Core.Net;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Stt;

/// <summary>
/// How much of a transcription call is the upload: the most that streaming the upload during the hold could save. Real
/// network calls, skipped unless YOLIME_LIVE=1 (well under a cent). Prints per-call timings to the test output.
/// </summary>
/// <remarks>
/// "Body sent" is when the last byte was handed to the socket. With the default send buffer a small body sits in the
/// kernel and that time hides the wire time, so this client shrinks the buffer to 8 KB: the body then counts as sent
/// only once all but its last 8 KB have left. Results are in docs/superpowers/plans/2026-10-28-latency.md.
/// </remarks>
public class LiveUploadShareTests
{
    private const int SendBuffer = 8 * 1024;

    [Theory]
    [InlineData("speech-short.wav")]
    [InlineData("speech-in-long-noise.wav")]
    public async Task UploadIsASmallShareOfTheCall(string fixture)
    {
        if (Environment.GetEnvironmentVariable("YOLIME_LIVE") != "1") Assert.Skip("set YOLIME_LIVE=1 to call the real STT API");
        var ep = SttEndpoint.Resolve(new YolimeSettings(), new EnvironmentApiKeySource());
        var pcm = WavFile.ReadPcm(Fixtures.Audio(fixture));
        using var http = HttpClients.Create(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, SendBufferSize = SendBuffer };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });
        var shares = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            await using var session = new BatchTranscriptionSession(http, ep, new SttSessionOptions([]), new RecordingLog());
            session.PushAudio(pcm);
            var r = await session.CompleteAsync(TestContext.Current.CancellationToken);
            var line = $"{fixture}: {pcm.Length} bytes, body sent at {r.BodySentMs} ms, first byte at {r.FirstByteMs} ms, new connection {r.NewConnection}";
            TestContext.Current.TestOutputHelper?.WriteLine(line);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "yolime-upload-share.txt"), line + Environment.NewLine);
            // Warm connections only: a cold one also counts the TCP and TLS handshakes as "upload".
            if (!r.NewConnection) shares.Add((double)r.BodySentMs / Math.Max(1, r.FirstByteMs));
        }
        Assert.NotEmpty(shares);
        // Not a target, a tripwire: if uploading ever becomes a large part of the call, streaming is worth revisiting.
        Assert.True(shares.Average() < 0.25, $"upload was {shares.Average():P0} of the call");
    }
}
