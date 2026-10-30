using Yolime.Core.Audio;

namespace Yolime.Core.Tests.Audio;

/// <summary>
/// The streamed upload must send exactly what <see cref="SilenceTrim.Trim"/> would: the same lead, the same trailing pad,
/// so streaming brings back neither the clipped soft starts nor the phrases transcribers invent from long silence.
/// </summary>
public class StreamTrimmerTests
{
    private const float Threshold = 0.0025f;

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

    public static TheoryData<string, int> Shapes() => new()
    {
        { "long lead, long trail", 0 },
        { "short lead, long trail", 1 },
        { "long lead, short trail", 2 },
        { "gap mid-speech, long trail", 3 },
        { "speech to the end", 4 },
        { "partial last frame after a long trail", 5 },
        { "partial last frame after a short trail", 6 },
    };

    private static byte[] Shape(int i) => i switch
    {
        0 => Pcm((3000, 0), (1500, 0.2), (2500, 0)),
        1 => Pcm((600, 0), (1500, 0.2), (2500, 0)),
        2 => Pcm((3000, 0), (1500, 0.2), (700, 0)),
        3 => Pcm((1500, 0), (800, 0.2), (1800, 0), (900, 0.2), (3000, 0)),
        4 => Pcm((2000, 0), (2000, 0.2)),
        5 => Pcm((2000, 0), (1000, 0.2), (2050, 0)),
        _ => Pcm((2000, 0), (1000, 0.2), (550, 0)),
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheFinalRange_IsExactlyWhatSilenceTrimKeeps(string name, int shape)
    {
        _ = name;
        var pcm = Shape(shape);
        var expected = SilenceTrim.Trim(pcm, Threshold).ToArray();
        foreach (var chunk in new[] { 3200, 1000, 4410, 777 })
        {
            var t = new StreamTrimmer(Threshold);
            for (var o = 0; o < pcm.Length; o += chunk) t.Push(pcm.AsSpan(o, Math.Min(chunk, pcm.Length - o)));
            var (start, end) = t.Final();
            Assert.Equal(expected, pcm[(int)start..(int)end]);
        }
    }

    [Fact]
    public void WhileLive_NothingAfterTheLastSoundIsSendable_AndNothingBeforeTheLead()
    {
        // 3 s of silence, 1 s of speech, then silence still arriving: the silence after the speech is held back.
        var pcm = Pcm((3000, 0), (1000, 0.2), (800, 0));
        var t = new StreamTrimmer(Threshold);
        t.Push(pcm);
        Assert.Equal(26 * 3200, t.Start); // 4 frames before the first sound at 30
        Assert.Equal(40 * 3200, t.SendableEnd); // up to the end of the last sound frame
    }

    [Fact]
    public void HeldSilence_IsReleased_WhenSoundResumes()
    {
        var t = new StreamTrimmer(Threshold);
        t.Push(Pcm((1000, 0.2), (700, 0)));
        Assert.Equal(10 * 3200, t.SendableEnd);
        t.Push(Pcm((500, 0.2)));
        Assert.Equal(22 * 3200, t.SendableEnd);
    }

    [Fact]
    public void BeforeAnySound_NothingIsSendable()
    {
        var t = new StreamTrimmer(Threshold);
        t.Push(Pcm((2000, 0)));
        Assert.Null(t.Start);
        Assert.Equal(0, t.SendableEnd);
    }

    [Fact]
    public void SendableEnd_NeverExceedsTheFinalEnd()
    {
        // The stream may only ever send bytes the final range keeps.
        var pcm = Shape(3);
        var t = new StreamTrimmer(Threshold);
        long maxLive = 0;
        for (var o = 0; o < pcm.Length; o += 3200)
        {
            t.Push(pcm.AsSpan(o, Math.Min(3200, pcm.Length - o)));
            maxLive = Math.Max(maxLive, t.SendableEnd);
        }
        Assert.True(maxLive <= t.Final().End);
    }
}
