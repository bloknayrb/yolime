namespace Yolime.Core.Hotkeys;

/// <summary>What the hook can tell about a foreground window, without any of its text.</summary>
/// <param name="Pid">The window's process; 0 when there's no foreground window (UAC, a desktop switch).</param>
/// <param name="ShellTray">The taskbar or the tray overflow (<see cref="IsShellTrayClass"/>).</param>
public readonly record struct ForegroundSample(int Pid, bool Visible, bool Cloaked, bool ShellTray)
{
    /// <summary>
    /// A window the user could be working in: shown, not cloaked (Start, Search and Quick Settings stay alive but
    /// cloaked once closed), and not the taskbar.
    /// </summary>
    public bool IsReal => Pid != 0 && Visible && !Cloaked && !ShellTray;

    public static bool IsShellTrayClass(string? className) =>
        className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
}

public enum ForegroundVerdict
{
    None,

    /// <summary>The user is in another app now.</summary>
    Switched,

    /// <summary>A pending switch ended back on the target: it was a flash.</summary>
    Settled,
}

/// <param name="PendingMs">For <see cref="ForegroundVerdict.Settled"/>: how long the switch was pending.</param>
public readonly record struct ForegroundResult(ForegroundVerdict Verdict, long PendingMs = 0);

/// <summary>
/// Whether the foreground really left the target app. Background processes briefly take the foreground with windows
/// nobody sees (a Sandbox probe caught rgnupdt.exe's hidden console holding it for about 67 ms), and a window can be
/// cloaked or hidden at the moment it's activated and appear just after (a UWP app launching, a virtual desktop swipe).
/// So any foreign foreground event starts a pending switch, and the decision is made at the hook's ticks, on the window
/// that's in front then: back on the target clears it; a real window elsewhere, once <c>settleMs</c> have passed, is a
/// switch; anything else waits.
/// </summary>
/// <remarks>Runs on the hook thread only.</remarks>
public sealed class ForegroundRule(int settleMs)
{
    /// <summary>For a hold: long enough for a flash to hand the foreground back, short enough not to be noticed.</summary>
    public const int HoldSettleMs = 200;

    private int _targetPid;
    private long? _pendingSince;

    public bool Pending => _pendingSince is not null;

    /// <summary>
    /// Pending, and still inside the settle window (plus one 100 ms tick): while this is true a hold doesn't announce
    /// itself. Bounded, because a hidden window that keeps the foreground leaves a switch pending for good.
    /// </summary>
    public bool Settling(long nowMs) => _pendingSince is { } since && nowMs - since <= settleMs + 100;

    /// <summary>A new hold or preview: its target app, and nothing pending.</summary>
    public void Reset(int targetPid)
    {
        _targetPid = targetPid;
        _pendingSince = null;
    }

    /// <summary>A foreground event.</summary>
    public ForegroundResult Observe(ForegroundSample window, long atMs)
    {
        if (window.Pid == _targetPid) return Clear(atMs);
        if (window.Pid == 0) return default;
        // With no settle, a real switch counts at once: the preview mustn't take keys typed in the new app.
        if (settleMs == 0 && window.IsReal) return Fire();
        _pendingSince ??= atMs;
        return default;
    }

    /// <summary>A tick, with the window that's in front now.</summary>
    public ForegroundResult Tick(ForegroundSample now, long atMs)
    {
        if (_pendingSince is not { } since) return default;
        if (now.Pid == _targetPid) return Clear(atMs);
        return now.IsReal && atMs - since >= settleMs ? Fire() : default;
    }

    private ForegroundResult Clear(long atMs)
    {
        if (_pendingSince is not { } since) return default;
        _pendingSince = null;
        return new(ForegroundVerdict.Settled, atMs - since);
    }

    private ForegroundResult Fire()
    {
        _pendingSince = null;
        return new(ForegroundVerdict.Switched);
    }
}
