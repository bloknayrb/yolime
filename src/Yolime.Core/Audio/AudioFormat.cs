namespace Yolime.Core.Audio;

/// <summary>The one PCM format used everywhere in Core: 16 kHz, mono, signed 16-bit little-endian.</summary>
public static class AudioFormat
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BytesPerMs = SampleRate * Channels * (BitsPerSample / 8) / 1000;
}

public readonly record struct AudioChunk(ReadOnlyMemory<byte> Pcm, float Rms)
{
    public long DurationMs => Pcm.Length / AudioFormat.BytesPerMs;
}

public enum CaptureError { AccessDenied, NoDevice, DeviceLost }

/// <summary>Thrown by an <see cref="IAudioSource"/> when the microphone fails.</summary>
public sealed class AudioCaptureException(CaptureError error) : Exception(error.ToString())
{
    public CaptureError Error { get; } = error;
}

public interface IAudioSource
{
    IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken ct);
}
