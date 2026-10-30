namespace Yolime.Core.Audio;

/// <summary>
/// Trims long silence from the start and end of a recording before it's uploaded: a little less to send and process,
/// and fewer of the phrases transcribers invent from silence. Deliberately timid, since cutting a word is worse than
/// sending silence: only edges longer than <see cref="MinTrimMs"/> go, judged at half the speech threshold so a soft
/// start or a quiet last word counts as sound, and a pad is kept either side (longer after the speech than before it).
/// </summary>
/// <remarks>The recording on disk (for Retry) and the speech gate's NoSpeech decision both use the untrimmed audio.</remarks>
public static class SilenceTrim
{
    public const int FrameMs = 100;
    public const int MinTrimMs = 1000;

    /// <summary>Kept before the first sound.</summary>
    public const int LeadPadMs = 400;

    /// <summary>Kept after the last sound: longer, because a mumbled last word after a pause can sit below the threshold.</summary>
    public const int TrailPadMs = 1000;

    public static ReadOnlyMemory<byte> Trim(ReadOnlyMemory<byte> pcm, float speechThreshold)
    {
        var frameBytes = FrameMs * AudioFormat.BytesPerMs;
        var frames = pcm.Length / frameBytes;
        if (frames == 0) return pcm;
        var threshold = speechThreshold * 0.5f;
        bool Sound(int f) => Rms.Of(pcm.Span.Slice(f * frameBytes, frameBytes)) >= threshold;

        var first = 0;
        while (first < frames && !Sound(first)) first++;
        if (first == frames) return pcm; // nothing but silence: the gate decides what that means
        var last = frames - 1;
        while (last > first && !Sound(last)) last--;

        var lead = first * FrameMs >= MinTrimMs ? Math.Max(0, first - LeadPadMs / FrameMs) * frameBytes : 0;
        // A trailing partial frame is kept when the tail isn't trimmed, and dropped with the silence when it is.
        var trailFrames = frames - 1 - last;
        var end = trailFrames * FrameMs >= MinTrimMs ? Math.Min(frames, last + 1 + TrailPadMs / FrameMs) * frameBytes : pcm.Length;
        return lead == 0 && end == pcm.Length ? pcm : pcm[lead..end];
    }
}
