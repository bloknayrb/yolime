using Yolime.Core.Audio;

namespace Yolime.Core.Tests.Audio;

public sealed class WavFileTests : IDisposable
{
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string WriteWav(byte[] pcm, long headerDataBytes, int sampleRate = 16000)
    {
        var path = Path.Combine(_root.Dir, Guid.NewGuid().ToString("N") + ".wav");
        using var fs = File.Create(path);
        WavFile.WriteHeader(fs, headerDataBytes, sampleRate);
        fs.Write(pcm);
        return path;
    }

    [Fact]
    public void StreamingHeader_SaysTheLengthIsUnknown_AndOtherwiseMatchesTheNormalOne()
    {
        // The form OpenAI accepted for a streamed upload (verified live): RIFF and data sizes of 0xFFFFFFFF.
        var streaming = WavFile.StreamingHeader();
        using var ms = new MemoryStream();
        WavFile.WriteHeader(ms, 0);
        var normal = ms.ToArray();
        Assert.Equal(WavFile.HeaderBytes, streaming.Length);
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(streaming, 4));
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(streaming, 40));
        Assert.Equal(normal[8..40], streaming[8..40]);
        Assert.Equal("RIFF"u8.ToArray(), streaming[..4]);
    }

    [Fact]
    public void Header_RoundTrips()
    {
        var pcm = Enumerable.Range(0, 640).Select(i => (byte)i).ToArray();
        Assert.Equal(pcm, WavFile.ReadPcm(WriteWav(pcm, pcm.Length)));
    }

    [Fact]
    public void ReadPcm_ZeroSizedHeader_UsesFileLength()
    {
        var pcm = new byte[3200];
        pcm[100] = 7;
        Assert.Equal(pcm, WavFile.ReadPcm(WriteWav(pcm, 0)));
    }

    [Fact]
    public void ReadPcm_WrongFormat_Throws()
    {
        var path = WriteWav(new byte[100], 100, sampleRate: 44100);
        Assert.Throws<InvalidDataException>(() => WavFile.ReadPcm(path));
    }

    [Fact]
    public void ReadPcm_WhileWriterOpen_Succeeds()
    {
        var path = Path.Combine(_root.Dir, "open.wav");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        WavFile.WriteHeader(writer, 0);
        writer.Write(new byte[320]);
        writer.Flush();

        Assert.Equal(320, WavFile.ReadPcm(path).Length);
    }

    [Fact]
    public void ReadPcm_Fixture_IsSixteenKMono()
    {
        var pcm = WavFile.ReadPcm(Fixtures.Audio("silence-2s.wav"));
        Assert.Equal(2 * 16000 * 2, pcm.Length);
    }
}
