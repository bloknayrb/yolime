using Yolime.Core.Output;

namespace Yolime.App.Native;

/// <summary>
/// The pending paste's wait: after the user's own click (seen by the hook, never injected), once the button is up,
/// look at what has focus. It counts only in the same app and only if it isn't positively read-only; the executor then
/// re-plans against it, so password, admin and terminal rules still apply.
/// </summary>
internal sealed class FocusWaiter(Func<HookThread> hooksLater, TargetReader targets) : IEditableFocusWaiter
{
    public async Task<TargetSnapshot?> WaitAsync(int processId, TimeSpan limit, CancellationToken ct)
    {
        var hooks = hooksLater();
        hooks.WatchMouse(true);
        try
        {
            var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
            var seen = hooks.LastUserMouseDownMs;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(100, ct);
                var click = hooks.LastUserMouseDownMs;
                // Pasting on mouse-down can turn into a drag-select: wait for the button to come up.
                if (click == seen || User32.GetAsyncKeyState(0x01) < 0) continue;
                seen = click;
                var snapshot = targets.Snapshot();
                // UIA has to have answered (a timeout leaves no focus id): otherwise a click back on the same read-only
                // pane would pass as "not known to be read-only".
                if (snapshot.ProcessId == processId && snapshot.FocusRuntimeId is not null && snapshot.IsEditable != false) return snapshot;
            }
            return null;
        }
        finally
        {
            hooks.WatchMouse(false);
        }
    }
}
