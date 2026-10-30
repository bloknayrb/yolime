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

    /// <summary>
    /// The most hovering can add: the card still fails open within <see cref="DeadlineMs"/> + this, even with the pointer
    /// parked on it or a pointer-leave that never arrives.
    /// </summary>
    public const long MaxHoverMs = 40_000;

    private const int VkEnter = 0x0D, VkEscape = 0x1B, VkC = 0x43;
    private const int VkPageUp = 0x21, VkPageDown = 0x22, VkUp = 0x26, VkDown = 0x28;

    private readonly HashSet<int> _held = [];
    // No settle: a real switch ends the card at once. A hidden flash only waits for the next tick's look.
    private readonly ForegroundRule _switch = new(settleMs: 0);
    private long _deadline;
    // While the pointer is on the card the deadline doesn't advance: since when, and how much hovering has added so far.
    private long _hoverSince = -1, _extended;

    public bool Active { get; private set; }

    public void Arm(long nowMs, int targetPid)
    {
        (_deadline, Active, _hoverSince, _extended) = (nowMs + DeadlineMs, true, -1, 0);
        _switch.Reset(targetPid);
    }

    public void Disarm() => (Active, _hoverSince) = (false, -1);

    /// <summary>The pointer entered or left the card: hovering pauses the deadline, within <see cref="MaxHoverMs"/>.</summary>
    public void Hover(bool over, long nowMs)
    {
        if (!Active) return;
        if (over && _hoverSince < 0) _hoverSince = nowMs;
        else if (!over && _hoverSince >= 0)
        {
            var added = HoverAdds(nowMs);
            (_deadline, _extended, _hoverSince) = (_deadline + added, _extended + added, -1);
        }
    }

    /// <summary>Time left and whether it's paused, for the card's hint (a duration: the UI keeps a different clock).</summary>
    public (long RemainingMs, bool Paused) Clock(long nowMs) =>
        (Math.Max(0, Deadline(nowMs) - nowMs), _hoverSince >= 0 && _extended + (nowMs - _hoverSince) < MaxHoverMs);

    private long HoverAdds(long nowMs) => _hoverSince < 0 ? 0 : Math.Min(nowMs - _hoverSince, MaxHoverMs - _extended);

    private long Deadline(long nowMs) => _deadline + HoverAdds(nowMs);

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
        if (nowMs >= Deadline(nowMs)) return (false, End());
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
        return nowMs >= Deadline(nowMs) || _switch.Tick(foreground, nowMs).Verdict == ForegroundVerdict.Switched ? End() : null;
    }

    private PreviewSignal End()
    {
        (Active, _hoverSince) = (false, -1);
        return PreviewSignal.Expired;
    }
}
