namespace Yolime.Core.Audio;

/// <summary>
/// <see cref="SilenceTrim"/> for audio that's still arriving: it holds the recording and says which bytes can already be
/// sent without ever sending one that the final trim would drop. The final range is exactly <c>SilenceTrim.Trim</c>'s,
/// so a streamed upload sends the same audio a batch one would.
/// </summary>
/// <remarks>
/// Frames sit on Trim's 100 ms grid from the start of the recording, judged at half the speech threshold as Trim judges
/// them. Silence after the last sound is held back until sound resumes or the recording ends, when Trim's trailing rule
/// decides how much of it goes. Thread-safe: one writer (the capture loop), any number of readers (the request body).
/// </remarks>
public sealed class StreamTrimmer(float speechThreshold)
{
    private const int FrameBytes = SilenceTrim.FrameMs * AudioFormat.BytesPerMs;

    private readonly object _gate = new();
    private byte[] _pcm = new byte[64 * 1024];
    private int _length;
    private int _judged; // full frames already judged
    private int _firstSound = -1;
    private int _lastSound = -1;

    /// <summary>The recording's length so far, in bytes.</summary>
    public long Length
    {
        get { lock (_gate) return _length; }
    }

    /// <summary>Where the upload starts, once there has been a sound; null before.</summary>
    public long? Start
    {
        get { lock (_gate) return _firstSound < 0 ? null : LeadLocked(); }
    }

    /// <summary>How far the upload can go now: the end of the last sound frame (held-back silence excluded).</summary>
    public long SendableEnd
    {
        get { lock (_gate) return _lastSound < 0 ? 0 : (long)(_lastSound + 1) * FrameBytes; }
    }

    public void Push(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (_length + pcm.Length > _pcm.Length) Array.Resize(ref _pcm, Math.Max(_pcm.Length * 2, _length + pcm.Length));
            pcm.CopyTo(_pcm.AsSpan(_length));
            _length += pcm.Length;
            var threshold = speechThreshold * 0.5f;
            for (; (_judged + 1) * FrameBytes <= _length; _judged++)
            {
                if (Rms.Of(_pcm.AsSpan(_judged * FrameBytes, FrameBytes)) < threshold) continue;
                if (_firstSound < 0) _firstSound = _judged;
                _lastSound = _judged;
            }
        }
    }

    /// <summary>Copies recorded bytes from <paramref name="offset"/>; returns how many it copied.</summary>
    public int CopyTo(long offset, Span<byte> destination)
    {
        lock (_gate)
        {
            var n = (int)Math.Min(destination.Length, _length - offset);
            if (n <= 0) return 0;
            _pcm.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }
    }

    /// <summary>The whole recording, for the batch fallback (which trims it itself).</summary>
    public byte[] All()
    {
        lock (_gate) return _pcm.AsSpan(0, _length).ToArray();
    }

    /// <summary>The recording is over: the range <see cref="SilenceTrim.Trim"/> keeps.</summary>
    public (long Start, long End) Final()
    {
        lock (_gate)
        {
            if (_firstSound < 0) return (0, _length); // nothing but silence: Trim leaves it alone
            var frames = _length / FrameBytes;
            var trailFrames = frames - 1 - _lastSound;
            long end = trailFrames * SilenceTrim.FrameMs >= SilenceTrim.MinTrimMs
                ? (long)Math.Min(frames, _lastSound + 1 + SilenceTrim.TrailPadMs / SilenceTrim.FrameMs) * FrameBytes
                : _length;
            return (LeadLocked(), end);
        }
    }

    private long LeadLocked() =>
        _firstSound * SilenceTrim.FrameMs >= SilenceTrim.MinTrimMs
            ? (long)Math.Max(0, _firstSound - SilenceTrim.LeadPadMs / SilenceTrim.FrameMs) * FrameBytes
            : 0;
}
