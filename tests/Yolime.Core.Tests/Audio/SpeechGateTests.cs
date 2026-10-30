using Yolime.Core.Audio;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Audio;

public class SpeechGateTests
{
    private static async Task<bool> HeardSpeech(string fixture)
    {
        var gate = new SpeechGate((float)new YolimeSettings().SilenceRms);
        await foreach (var c in new FileAudioSource(Fixtures.Audio(fixture)).ReadAsync(TestContext.Current.CancellationToken))
            gate.Add(c);
        return gate.HeardSpeech;
    }

    [Theory]
    [InlineData("silence-2s.wav")]
    [InlineData("noise-floor-5s.wav")]
    public async Task Gate_Silence_And_NoiseFloor_NotSpeech(string fixture) => Assert.False(await HeardSpeech(fixture));

    [Theory]
    [InlineData("speech-short.wav")]
    [InlineData("speech-quiet.wav")]
    [InlineData("speech-in-long-noise.wav")]
    public async Task Gate_Speech_Quiet_And_InLongNoise_AreSpeech(string fixture) => Assert.True(await HeardSpeech(fixture));

    [Fact]
    public void Gate_CountsDuration_NotChunks()
    {
        var gate = new SpeechGate(0.01f);
        var loud = new byte[640];
        // 20 ms chunks: 14 of them is 280 ms, under the 300 ms bar; the 15th crosses it.
        for (var i = 0; i < 14; i++) gate.Add(new AudioChunk(loud, 0.5f));
        Assert.False(gate.HeardSpeech);
        gate.Add(new AudioChunk(loud, 0.5f));
        Assert.True(gate.HeardSpeech);
    }
}
