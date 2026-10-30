namespace Yolime.Core.Hotkeys;

/// <summary>
/// Settings' "press the key you want to hold" box: the next real key-down is captured and swallowed, with its
/// auto-repeats and key-up, so pressing Pause or Right Shift there does nothing else. It is armed only on request,
/// only while a yolime window is in front, and for <see cref="WindowMs"/> at most. Runs inside the hook callback.
/// </summary>
/// <remarks>Key codes stay in this class and the page that asked: like the reducer's, they are never logged.</remarks>
public sealed class KeyCapture
{
    public const long WindowMs = 10_000;

    /// <summary>
    /// How long the captured key may go unheard before it stops being swallowed. A held key repeats every ~30 ms once
    /// the keyboard delay (at most 1 s) has passed, so 1.5 s of silence means it was released.
    /// </summary>
    public const long QuietMs = 1_500;

    private long _armedAtMs;
    private int? _swallowing;
    private long _lastSwallowedMs;

    public bool Active { get; private set; }

    /// <summary>Armed, or still swallowing the captured key's repeats and key-up: the hook must keep asking.</summary>
    public bool Busy => Active || _swallowing is not null;

    public void Arm(long nowMs)
    {
        Active = true;
        _armedAtMs = nowMs;
        _swallowing = null;
    }

    public void Disarm() => Active = false;

    /// <summary>
    /// Stops swallowing the captured key after <see cref="QuietMs"/> without hearing it. Called on the hook's tick: a
    /// key-up the hook never saw (released over an admin window or the secure desktop) would otherwise eat that key's
    /// next press everywhere. Its physical state can't be asked instead: Windows doesn't update the key state for
    /// input a hook blocks.
    /// </summary>
    public void ReleaseIfQuiet(long nowMs)
    {
        if (_swallowing is not null && nowMs - _lastSwallowedMs >= QuietMs) _swallowing = null;
    }

    /// <param name="ownForeground">Whether a yolime window is in front; if not, capture ends at once.</param>
    /// <returns>Whether to swallow the key, and the captured key code on the key-down that was captured.</returns>
    public (bool Taken, int? Captured) Key(int vk, bool up, bool injected, bool ownForeground, long nowMs)
    {
        // yolime's own injected input (a paste's Ctrl+V) is never swallowed, even if the captured key was Ctrl.
        if (_swallowing == vk && !injected)
        {
            _lastSwallowedMs = nowMs;
            if (up) _swallowing = null;
            return (true, null);
        }
        if (!Active || injected) return (false, null);
        if (!ownForeground || nowMs - _armedAtMs >= WindowMs)
        {
            Active = false;
            return (false, null);
        }
        if (up) return (false, null);
        Active = false;
        _swallowing = vk;
        _lastSwallowedMs = nowMs;
        return (true, vk);
    }

    /// <returns>True once, when an armed capture runs out of time.</returns>
    public bool Tick(long nowMs)
    {
        if (!Active || nowMs - _armedAtMs < WindowMs) return false;
        Active = false;
        return true;
    }
}
