using Yolime.Core.Audio;
using Yolime.Core.Net;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Stt;

/// <summary>
/// The streaming session itself against OpenAI, fed in real time like a mic (100 ms of audio every 100 ms). Real
/// network calls, skipped unless YOLIME_LIVE=1; the long one takes over a minute and costs about a cent.
/// </summary>
public class LiveStreamingUploadTests
{
    /// <summary>The session's log lines (content-free by construction), so a fallback's reason shows in the failure.</summary>
    private sealed class LineLog : Yolime.Core.Logging.ILog
    {
        public List<string> Lines { get; } = [];
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public void Write(Yolime.Core.Logging.LogEvent evt, params ReadOnlySpan<Yolime.Core.Logging.LogField> fields)
        {
            var parts = new List<string> { $"{_clock.ElapsedMilliseconds}ms", evt.ToString() };
            foreach (var f in fields) parts.Add(f.Format());
            lock (Lines) Lines.Add(string.Join(' ', parts));
        }
    }

    private static async Task<SttResult> StreamAsync(SttEndpoint ep, byte[] pcm)
    {
        var log = new LineLog();
        try
        {
            return await StreamCoreAsync(ep, pcm, log);
        }
        finally
        {
            File.AppendAllLines(Path.Combine(Path.GetTempPath(), "yolime-stream-live.txt"), log.Lines);
        }
    }

    private static async Task<SttResult> StreamCoreAsync(SttEndpoint ep, byte[] pcm, LineLog log)
    {
        await using var session = new StreamingTranscriptionSession(HttpClients.Shared, ep, new SttSessionOptions([], TrimBelow: 0.0025f, Live: true), log, TimeProvider.System);
        for (var o = 0; o < pcm.Length; o += 3200)
        {
            session.PushAudio(pcm.AsMemory(o, Math.Min(3200, pcm.Length - o)));
            await Task.Delay(100);
        }
        return await session.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("gpt-4o-transcribe")]
    [InlineData("whisper-1")]
    public async Task AStreamedHold_IsTranscribedWhole(string model)
    {
        if (Environment.GetEnvironmentVariable("YOLIME_LIVE") != "1") Assert.Skip("set YOLIME_LIVE=1 to call the real STT API");
        var ep = SttEndpoint.Resolve(new YolimeSettings(), new EnvironmentApiKeySource()) with { Model = model };
        // Leading and trailing silence, so the trim and the held-back tail are exercised too.
        var pcm = new byte[1500 * AudioFormat.BytesPerMs].Concat(WavFile.ReadPcm(Fixtures.Audio("speech-short.wav"))).Concat(new byte[2000 * AudioFormat.BytesPerMs]).ToArray();
        var r = await StreamAsync(ep, pcm);

        Assert.Equal(SttUpload.Streamed, r.Upload); // not a fallback: no 411, no 400 for the unknown length
        var text = r.Text.ToLowerInvariant();
        Assert.Contains("quick", text);
        Assert.Contains("lazy dog", text); // the last words: the tail of the stream was heard
        // Only the server's processing is left after key-up.
        Assert.True(r.FirstByteMs - r.BodySentMs < 3000, $"{r.FirstByteMs - r.BodySentMs} ms after the body");
    }

    [Fact]
    public async Task AMinuteOfContinuousSpeech_IsStreamedInOneRequest()
    {
        // No pause to park on: an edge limit on a request's total duration, rather than idleness, would show here.
        if (Environment.GetEnvironmentVariable("YOLIME_LIVE") != "1") Assert.Skip("set YOLIME_LIVE=1 to call the real STT API");
        var ep = SttEndpoint.Resolve(new YolimeSettings(), new EnvironmentApiKeySource());
        var speech = WavFile.ReadPcm(Fixtures.Audio("speech-short.wav"));
        var r = await StreamAsync(ep, Enumerable.Repeat(speech, 18).SelectMany(x => x).ToArray());
        Assert.Equal(SttUpload.Streamed, r.Upload);
        Assert.Contains("lazy dog", r.Text.ToLowerInvariant());
    }

    [Fact]
    public async Task ATwentySecondPauseMidHold_IsParked_AndTheStreamRestarts()
    {
        // OpenAI drops a body idle 15 s or more (measured): the stream parks in the pause and re-sends after it.
        if (Environment.GetEnvironmentVariable("YOLIME_LIVE") != "1") Assert.Skip("set YOLIME_LIVE=1 to call the real STT API");
        var ep = SttEndpoint.Resolve(new YolimeSettings(), new EnvironmentApiKeySource());
        var speech = WavFile.ReadPcm(Fixtures.Audio("speech-short.wav"));
        var r = await StreamAsync(ep, [.. speech, .. new byte[20_000 * AudioFormat.BytesPerMs], .. speech]);
        Assert.Equal(SttUpload.Streamed, r.Upload);
        Assert.Contains("lazy dog", r.Text.ToLowerInvariant());
    }
}
