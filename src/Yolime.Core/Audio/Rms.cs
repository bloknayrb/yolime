using System.Runtime.InteropServices;

namespace Yolime.Core.Audio;

public static class Rms
{
    /// <summary>Root mean square of s16le samples, scaled to 0..1.</summary>
    public static float Of(ReadOnlySpan<byte> pcm16)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return (float)(Math.Sqrt(sum / samples.Length) / 32768.0);
    }
}

/// <summary>
/// Decides whether a recording contains speech. It counts voiced *duration* (chunks at or above the threshold)
/// rather than a whole-clip average, which a short utterance inside a long hold would dilute away.
/// </summary>
public sealed class SpeechGate(float threshold, long minVoicedMs = 300)
{
    private long _voicedMs;

    public void Add(AudioChunk chunk)
    {
        if (chunk.Rms >= threshold) _voicedMs += chunk.DurationMs;
    }

    public bool HeardSpeech => _voicedMs >= minVoicedMs;
}
