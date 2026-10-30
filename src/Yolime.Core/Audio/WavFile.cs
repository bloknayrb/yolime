using System.Buffers.Binary;
using System.Text;

namespace Yolime.Core.Audio;

/// <summary>Minimal RIFF/PCM read and write, so Core doesn't need an audio library.</summary>
public static class WavFile
{
    public const int HeaderBytes = 44;

    /// <summary>A header for a recording still being written: sizes of 0xFFFFFFFF ("unknown"), as a streamed upload needs.</summary>
    public static byte[] StreamingHeader()
    {
        var header = new byte[HeaderBytes];
        using (var ms = new MemoryStream(header)) WriteHeader(ms, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), uint.MaxValue);
        return header;
    }

    public static void WriteHeader(Stream s, long dataBytes, int sampleRate = AudioFormat.SampleRate)
    {
        Span<byte> h = stackalloc byte[HeaderBytes];
        var data = (uint)Math.Min(dataBytes, uint.MaxValue - 36);
        Encoding.ASCII.GetBytes("RIFF", h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 36 + data);
        Encoding.ASCII.GetBytes("WAVEfmt ", h[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], AudioFormat.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], sampleRate * AudioFormat.Channels * AudioFormat.BitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], AudioFormat.Channels * AudioFormat.BitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], AudioFormat.BitsPerSample);
        Encoding.ASCII.GetBytes("data", h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], data);
        s.Write(h);
    }

    /// <summary>
    /// Reads the PCM from a 16 kHz mono s16 WAV. A header whose sizes were never written (a crash mid-recording)
    /// falls back to the file length. Opens with ReadWrite|Delete sharing so it works while the recorder still has
    /// the file open.
    /// </summary>
    public static byte[] ReadPcm(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> h = stackalloc byte[HeaderBytes];
        if (fs.Read(h) != HeaderBytes
            || !h[..4].SequenceEqual("RIFF"u8)
            || !h[8..16].SequenceEqual("WAVEfmt "u8)
            || !h[36..40].SequenceEqual("data"u8))
            throw new InvalidDataException("not a canonical PCM WAV");
        if (BinaryPrimitives.ReadInt16LittleEndian(h[20..]) != 1
            || BinaryPrimitives.ReadInt16LittleEndian(h[22..]) != AudioFormat.Channels
            || BinaryPrimitives.ReadInt32LittleEndian(h[24..]) != AudioFormat.SampleRate
            || BinaryPrimitives.ReadInt16LittleEndian(h[34..]) != AudioFormat.BitsPerSample)
            throw new InvalidDataException("expected 16 kHz mono 16-bit PCM");

        var available = fs.Length - HeaderBytes;
        long declared = BinaryPrimitives.ReadUInt32LittleEndian(h[40..]);
        var size = declared == 0 || declared > available ? available : declared;
        size -= size % 2;
        var pcm = new byte[size];
        fs.ReadExactly(pcm);
        return pcm;
    }
}
