namespace Yolime.Core.Hotkeys;

/// <summary>What a key or event did to the preview card. <see cref="Expired"/>: it ended without a choice.</summary>
public enum PreviewSignal { Insert, Copy, Discard, Expired, ScrollUp, ScrollDown, PageUp, PageDown }

/// <summary>
/// The keyboard side of the preview card, consulted by the hook before the hold machine. While armed it takes
/// Enter, Esc and C (and the scroll keys) away from the focused app, so the card works without ever taking focus.
/// </summary>
/// <remarks>
/// It fails open: any other key, a shortcut, a click off the card, a switch to another app or the deadline ends it, so
/// a forgotten card can't keep eating the user's typing. A swallowed key stays swallowed through its auto-repeat and
/// key-up, whatever happens to the card meanwhile, so the app never sees half a keystroke. Injected keys (password
/// managers' auto-type) are never touched. Key codes stop here: callers get signals only.
/// </remarks>
public sealed class PreviewCapture(int holdVk)
{
    /// <summary>The longest the card may hold keys. Enforced here, on the hook thread, not by the UI.</summary>
    public const long DeadlineMs = 20_000;

    private const int VkEnter = 0x0D, VkEscape = 0x1B, VkC = 0x43;
    private const int VkPageUp = 0x21, VkPageDown = 0x22, VkUp = 0x26, VkDown = 0x28;

    private readonly HashSet<int> _held = [];
    // No settle: a real switch ends the card at once. A hidden flash only waits for the next tick's look.
    private readonly ForegroundRule _switch = new(settleMs: 0);
    private long _deadline;

    public bool Active { get; private set; }

    public void Arm(long nowMs, int targetPid)
    {
        (_deadline, Active) = (nowMs + DeadlineMs, true);
        _switch.Reset(targetPid);
    }

    public void Disarm() => Active = false;

    /// <param name="modifiers">Ctrl, Alt or a Windows key is down (Shift doesn't count: C is C either way).</param>
    public (bool Swallow, PreviewSignal? Signal) Key(int vk, bool up, bool injected, nint extraInfo, bool modifiers, long nowMs)
    {
        if (injected || extraInfo == HookReducer.OwnInputMarker) return (false, null);
        if (_held.Contains(vk))
        {
            // The rest of a keystroke we took: its auto-repeats and its key-up.
            if (up) _held.Remove(vk);
            return (true, null);
        }
        if (!Active || up) return (false, null);
        if (nowMs >= _deadline) return (false, End());
        if (vk == holdVk || vk is 0x10 or 0xA0 or 0xA1) return (false, null);
        if (modifiers) return (false, End());

        PreviewSignal? signal = vk switch
        {
            VkEnter => PreviewSignal.Insert,
            VkEscape => PreviewSignal.Discard,
            VkC => PreviewSignal.Copy,
            VkUp => PreviewSignal.ScrollUp,
            VkDown => PreviewSignal.ScrollDown,
            VkPageUp => PreviewSignal.PageUp,
            VkPageDown => PreviewSignal.PageDown,
            _ => null,
        };
        if (signal is null) return (false, End());
        _held.Add(vk);
        if (signal is PreviewSignal.Insert or PreviewSignal.Discard or PreviewSignal.Copy) Active = false;
        return (true, signal);
    }

    /// <param name="onCard">The click is on the card itself (its buttons answer it).</param>
    public PreviewSignal? MouseDown(bool onCard) => Active && !onCard ? End() : null;

    public PreviewSignal? Foreground(ForegroundSample window, long nowMs) =>
        Active && _switch.Observe(window, nowMs).Verdict == ForegroundVerdict.Switched ? End() : null;

    /// <param name="foreground">The window in front now.</param>
    public PreviewSignal? Tick(long nowMs, ForegroundSample foreground)
    {
        if (!Active) return null;
        return nowMs >= _deadline || _switch.Tick(foreground, nowMs).Verdict == ForegroundVerdict.Switched ? End() : null;
    }

    private PreviewSignal End()
    {
        Active = false;
        return PreviewSignal.Expired;
    }
}
