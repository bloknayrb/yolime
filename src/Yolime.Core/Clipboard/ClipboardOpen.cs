namespace Yolime.Core.Clipboard;

/// <summary>
/// Counts the time spent opening the clipboard, and the retries, for one paste (or one restore). PR7 suspected the
/// open retries of its 5 s stall; the save and set times include the open, but can't say whether it went on retries.
/// </summary>
/// <remarks>
/// Ambient, through <see cref="AsyncLocal{T}"/>, so the opens deep in the clipboard code need no extra parameter. It
/// is begun once in the caller's own method body and only ever added to, never replaced: a value set inside an awaited
/// callee doesn't flow back to its caller. The background restore begins a meter of its own; anything else recording
/// after <see cref="Close"/> is dropped.
/// </remarks>
public sealed class ClipboardOpenMeter
{
    private static readonly AsyncLocal<ClipboardOpenMeter?> Ambient = new();

    private long _ticks;
    private int _retries;
    private volatile bool _closed;

    public static ClipboardOpenMeter? Current => Ambient.Value;

    /// <summary>Starts a meter for the calling flow and what it goes on to await.</summary>
    public static ClipboardOpenMeter Begin() => Ambient.Value = new ClipboardOpenMeter();

    internal void Add(TimeSpan spent, int retries)
    {
        if (_closed) return;
        Interlocked.Add(ref _ticks, spent.Ticks);
        Interlocked.Add(ref _retries, retries);
    }

    /// <summary>The totals so far; anything recorded after this is dropped.</summary>
    public (long Ms, int Retries) Close()
    {
        _closed = true;
        return ((long)TimeSpan.FromTicks(Interlocked.Read(ref _ticks)).TotalMilliseconds, Volatile.Read(ref _retries));
    }
}

public static class ClipboardOpen
{
    /// <summary>
    /// Tries <paramref name="tryOpen"/> until it succeeds, backing off per <see cref="ClipboardPolicy.OpenRetryDelay"/>,
    /// and throws <see cref="IOException"/> after <see cref="ClipboardPolicy.MaxOpenAttempts"/>. The time and the
    /// retries go to the current meter whatever the outcome: a clipboard that stays busy is the case worth seeing.
    /// </summary>
    public static async Task RetryAsync(Func<bool> tryOpen, TimeProvider time, CancellationToken ct)
    {
        var meter = ClipboardOpenMeter.Current;
        var start = time.GetTimestamp();
        var attempt = 0;
        try
        {
            for (; ; attempt++)
            {
                if (tryOpen()) return;
                if (attempt + 1 >= ClipboardPolicy.MaxOpenAttempts) throw new IOException("clipboard is busy");
                await Task.Delay(ClipboardPolicy.OpenRetryDelay(attempt), time, ct);
            }
        }
        finally
        {
            meter?.Add(time.GetElapsedTime(start), attempt);
        }
    }
}
