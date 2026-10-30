using System.Runtime.InteropServices;
using Yolime.Core;
using Yolime.Core.Capture;
using Yolime.Core.Settings;

namespace Yolime.App.Shell;

/// <summary>
/// yolime's own short, soft sounds, generated in code at startup (no audio assets) and played from unmanaged memory
/// that lives as long as the process: PlaySound reads the buffer after returning, and a managed array could move.
/// </summary>
internal sealed partial class ToneSounds : ISounds
{
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004;
    private const int Rate = 22_050;

    private readonly Func<YolimeSettings> _settings;
    private readonly Dictionary<SoundKind, nint> _sounds = [];
    private readonly nint[] _cues = new nint[4];

    public ToneSounds(Func<YolimeSettings> settings)
    {
        _settings = settings;
        _sounds[SoundKind.Start] = Wav([(880, 45)]);
        _sounds[SoundKind.Stop] = Wav([(660, 45)]);
        _sounds[SoundKind.Error] = Wav([(440, 90), (0, 40), (330, 120)]);
        _sounds[SoundKind.Warning] = Wav([(520, 60), (0, 60), (520, 60)]);
        // Mode cues: 1 tick for Dictate up to 4 for Raw.
        for (var n = 0; n < 4; n++) _cues[n] = Wav(Enumerable.Range(0, n + 1).SelectMany(_ => new[] { (1200, 25), (0, 70) }).ToArray());
    }

    public void Play(SoundKind sound)
    {
        if (_settings().Sounds && _sounds.TryGetValue(sound, out var wav)) PlaySoundW(wav, 0, SndMemory | SndAsync | SndNoDefault);
    }

    public void PlayModeCue(Mode mode)
    {
        var s = _settings();
        if (s.Sounds && s.ModeCycleCues) PlaySoundW(_cues[(int)mode], 0, SndMemory | SndAsync | SndNoDefault);
    }

    /// <summary>16-bit mono PCM, each tone faded in and out over 5 ms so it clicks less. Frequency 0 is silence.</summary>
    private static nint Wav((int Hz, int Ms)[] parts)
    {
        var samples = new List<short>();
        foreach (var (hz, ms) in parts)
        {
            var n = Rate * ms / 1000;
            var fade = Rate * 5 / 1000;
            for (var i = 0; i < n; i++)
            {
                var env = Math.Min(1.0, Math.Min(i, n - i) / (double)fade);
                samples.Add(hz == 0 ? (short)0 : (short)(Math.Sin(2 * Math.PI * hz * i / Rate) * env * 6000));
            }
        }
        var data = samples.Count * 2;
        using var ms2 = new MemoryStream();
        using (var w = new BinaryWriter(ms2))
        {
            w.Write("RIFF"u8);
            w.Write(36 + data);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(Rate);
            w.Write(Rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(data);
            foreach (var s in samples) w.Write(s);
        }
        var bytes = ms2.ToArray();
        var ptr = Marshal.AllocHGlobal(bytes.Length); // never freed: the sounds live as long as the app
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    [LibraryImport("winmm.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(nint sound, nint module, uint flags);
}
