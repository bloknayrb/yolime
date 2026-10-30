using System.Runtime.CompilerServices;

namespace Yolime.Core.Audio;

/// <summary>
/// Replays a WAV file as chunks. Used by tests, by Retry (re-reading a pending recording), and by the E2E build's
/// <c>--test-audio</c>. Only the App's use of it is gated behind <c>YOLIME_E2E</c>.
/// </summary>
public sealed class FileAudioSource(string wavPath, int chunkMs = 100) : IAudioSource
{
    public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var pcm = WavFile.ReadPcm(wavPath);
        var size = chunkMs * AudioFormat.BytesPerMs;
        for (var offset = 0; offset < pcm.Length; offset += size)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = pcm.AsMemory(offset, Math.Min(size, pcm.Length - offset));
            yield return new AudioChunk(chunk, Rms.Of(chunk.Span));
            await Task.Yield();
        }
    }
}
