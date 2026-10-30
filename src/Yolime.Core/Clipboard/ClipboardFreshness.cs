namespace Yolime.Core.Clipboard;

/// <summary>
/// Tracks, without content, whether the clipboard holds something the user copied recently. The App reports every
/// <c>WM_CLIPBOARDUPDATE</c> by sequence number; the executor reports the sequences it caused. Reply context may
/// use the clipboard only when the user copied it within <see cref="MaxAge"/>.
/// </summary>
/// <remarks>
/// The update message and the executor's report race (the message can arrive first), so judgment happens at query
/// time from both records rather than at either event. Content present at startup has no known age and is stale.
/// </remarks>
public sealed class ClipboardFreshness(TimeProvider time)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(120);
    private const int Remember = 32;

    private readonly Lock _lock = new();
    private readonly List<(uint Sequence, long At)> _changes = [];
    private readonly List<uint> _own = [];
    private readonly Dictionary<uint, long?> _restoredAt = [];

    public void OnChanged(uint sequence)
    {
        lock (_lock) Add(_changes, (sequence, time.GetTimestamp()));
    }

    /// <summary>A sequence yolime caused: a paste copy, a "Copied" result, a clear.</summary>
    public void MarkOwn(uint sequence)
    {
        lock (_lock) Add(_own, sequence);
    }

    /// <summary>A restore of the user's copy: it inherits the age of the copy it put back.</summary>
    public void MarkRestored(uint sequence)
    {
        lock (_lock)
        {
            Add(_own, sequence);
            _restoredAt[sequence] = LatestUserChange(before: sequence)?.At;
            while (_restoredAt.Count > Remember) _restoredAt.Remove(_restoredAt.Keys.Min());
        }
    }

    /// <returns>The age in ms when <paramref name="currentSequence"/> is a user copy younger than <see cref="MaxAge"/>.</returns>
    public long? UserCopyAgeMs(uint currentSequence)
    {
        lock (_lock)
        {
            long? at;
            if (_restoredAt.TryGetValue(currentSequence, out var inherited)) at = inherited;
            else if (_own.Contains(currentSequence)) return null;
            else
            {
                var i = _changes.FindLastIndex(c => c.Sequence == currentSequence);
                at = i >= 0 ? _changes[i].At : null;
            }

            if (at is not { } stamp) return null;
            var age = time.GetElapsedTime(stamp);
            return age <= MaxAge ? (long)age.TotalMilliseconds : null;
        }
    }

    private (uint Sequence, long At)? LatestUserChange(uint before)
    {
        for (var i = _changes.Count - 1; i >= 0; i--)
        {
            var c = _changes[i];
            if (c.Sequence < before && !_own.Contains(c.Sequence)) return c;
        }
        return null;
    }

    private static void Add<T>(List<T> list, T item)
    {
        list.Add(item);
        if (list.Count > Remember) list.RemoveAt(0);
    }
}
