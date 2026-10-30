using Yolime.Core.Audio;
using Yolime.Core.Net;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Stt;

/// <summary>Real network calls. Skipped unless YOLIME_LIVE=1; costs well under a cent per run.</summary>
public class LiveSttSmokeTests
{
    [Fact]
    public async Task SpeechFixture_Transcribes()
    {
        if (Environment.GetEnvironmentVariable("YOLIME_LIVE") != "1") Assert.Skip("set YOLIME_LIVE=1 to call the real STT API");

        var ep = SttEndpoint.Resolve(new YolimeSettings(), new EnvironmentApiKeySource());
        await using var session = new BatchTranscriptionSession(HttpClients.Shared, ep, new SttSessionOptions([]), new RecordingLog());
        session.PushAudio(WavFile.ReadPcm(Fixtures.Audio("speech-short.wav")));
        var result = await session.CompleteAsync(TestContext.Current.CancellationToken);

        var text = result.Text.ToLowerInvariant();
        Assert.Contains("quick", text);
        Assert.Contains("fox", text);
        Assert.Contains("lazy dog", text);
    }
}
