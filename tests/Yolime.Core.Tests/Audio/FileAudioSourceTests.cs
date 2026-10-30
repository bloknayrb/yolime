using Yolime.Core.Audio;

namespace Yolime.Core.Tests.Audio;

public class FileAudioSourceTests
{
    [Fact]
    public async Task FileAudioSource_YieldsChunksOf3200Bytes_LastMayBeShorter()
    {
        var chunks = new List<AudioChunk>();
        await foreach (var c in new FileAudioSource(Fixtures.Audio("speech-short.wav")).ReadAsync(TestContext.Current.CancellationToken))
            chunks.Add(c);

        var total = WavFile.ReadPcm(Fixtures.Audio("speech-short.wav")).Length;
        Assert.All(chunks.SkipLast(1), c => Assert.Equal(3200, c.Pcm.Length));
        Assert.InRange(chunks[^1].Pcm.Length, 1, 3200);
        Assert.Equal(total, chunks.Sum(c => c.Pcm.Length));
        Assert.Contains(chunks, c => c.Rms > 0.01f);
    }

    [Fact]
    public async Task FileAudioSource_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new FileAudioSource(Fixtures.Audio("speech-short.wav")).ReadAsync(cts.Token))
            {
                if (++seen == 2) cts.Cancel();
            }
        });
        Assert.Equal(2, seen);
    }
}
