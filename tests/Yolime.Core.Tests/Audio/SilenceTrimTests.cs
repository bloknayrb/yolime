using Yolime.Core.Audio;

namespace Yolime.Core.Tests.Audio;

public class SilenceTrimTests
{
    private const float Threshold = 0.0025f;

    /// <summary>PCM: (milliseconds, amplitude 0..1) segments of a 440 Hz tone; 0 is silence.</summary>
    private static byte[] Pcm(params (int Ms, double Amplitude)[] parts)
    {
        var samples = new List<short>();
        foreach (var (ms, amp) in parts)
        {
            var n = ms * AudioFormat.SampleRate / 1000;
            for (var i = 0; i < n; i++) samples.Add((short)(Math.Sin(2 * Math.PI * 440 * samples.Count / AudioFormat.SampleRate) * amp * 32767));
        }
        var bytes = new byte[samples.Count * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static int Ms(int bytes) => bytes / AudioFormat.BytesPerMs;

    [Fact]
    public void LongSilenceAtTheEdges_IsTrimmed_KeepingAPad()
    {
        var pcm = Pcm((3000, 0), (1000, 0.2), (3000, 0));
        var trimmed = SilenceTrim.Trim(pcm, Threshold);
        // 1 s of speech, 400 ms before it and 1 s after, give or take one 100 ms frame.
        Assert.InRange(Ms(trimmed.Length), 2400 - 100, 2400 + 100);
    }

    [Fact]
    public void ShortSilence_IsLeftAlone()
    {
        var pcm = Pcm((800, 0), (1000, 0.2), (900, 0));
        Assert.Equal(pcm.Length, SilenceTrim.Trim(pcm, Threshold).Length);
    }

    [Fact]
    public void ASoftStartAndAQuietLastWord_AreKept()
    {
        // Quieter than the speech threshold but above half of it: a soft onset, a trailing "thanks".
        var soft = Threshold * 0.7 * Math.Sqrt(2);
        // Longer than the pads, so only counting the soft parts as sound keeps them: without that, 2400 ms would remain.
        var pcm = Pcm((2000, 0), (1000, soft), (1000, 0.2), (1500, soft), (2000, 0));
        var trimmed = SilenceTrim.Trim(pcm, Threshold);
        // All 3500 ms of sound, plus 400 ms before and 1 s after.
        Assert.InRange(Ms(trimmed.Length), 4900 - 100, 4900 + 100);
    }

    [Fact]
    public void AllSilence_IsLeftForTheGate()
    {
        // NoSpeech is the speech gate's call, made on the untrimmed audio; the trim doesn't second-guess it.
        var pcm = Pcm((3000, 0));
        Assert.Equal(pcm.Length, SilenceTrim.Trim(pcm, Threshold).Length);
    }

    [Fact]
    public void AQuietWordAfterAPause_IsKeptByTheTrailingPad()
    {
        // "...done." then a 600 ms pause and a mumbled "thanks" below even half the threshold: only the pad can save it.
        var mumble = Threshold * 0.3 * Math.Sqrt(2);
        var pcm = Pcm((1500, 0), (1000, 0.2), (600, 0), (300, mumble), (2000, 0));
        var trimmed = SilenceTrim.Trim(pcm, Threshold);
        var speechEnd = (400 + 1000) * AudioFormat.BytesPerMs; // where the speech ends in the trimmed audio: 400 ms lead pad
        Assert.True(trimmed.Length >= speechEnd + 900 * AudioFormat.BytesPerMs, $"kept only {Ms(trimmed.Length)} ms");
    }
}
