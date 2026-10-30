using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Yolime.Core;
using Yolime.Core.Hotkeys;
using Yolime.Core.Logging;

namespace Yolime.App.Native;

/// <summary>
/// The low-level keyboard and mouse hooks, on a dedicated thread with its own message loop. Each event is reduced to
/// a <see cref="HotkeyKind"/> inside the callback (key codes go no further) and run through the hold machine
/// synchronously, because whether to swallow Shift or Esc must be answered there. Effects go to a worker thread:
/// opening the mic inside a hook callback could exceed the hook timeout, after which Windows silently drops the hook.
/// </summary>
internal sealed class HookThread : IDisposable
{
    private const uint WmRunAction = User32.WmApp + 1;
    private const int ReArmTicks = 10 * 60 * 10; // ten minutes of 100 ms ticks

    private int _holdVk;
    private HookReducer _reducer;
    private HoldStateMachine _machine;
    private readonly Func<Mode> _startingMode;
    private readonly ILog _log;
    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly Channel<HoldEffect> _effects = Channel.CreateUnbounded<HoldEffect>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new();
    // Rooted for the hooks' lifetime: a collected delegate would crash the process on the next event.
    private readonly User32.HookProc _keyboardProc;
    private readonly User32.HookProc _mouseProc;
    private readonly User32.WinEventProc _foregroundProc;
    private uint _threadId;
    private nint _keyboardHook;
    private nint _mouseHook;
    private nint _foregroundHook;
    private uint _capturePid;
    // The last foreground window that was a real one, for a key-down that lands during a hidden flash.
    private uint _lastRealPid;
    private readonly ForegroundRule _switch = new(ForegroundRule.HoldSettleMs);
    private PreviewCapture _preview;
    private readonly KeyCapture _capture = new();
    private TaskCompletionSource<CapturedKey?>? _captured;
    private (int Vk, HoldOptions Options)? _reconfigure;
    private bool _watchMouse;
    private long _lastUserMouseDownMs;
    private int _ticksSinceReArm;

    public HookThread(int holdVk, HoldStateMachine machine, Func<Mode> startingMode, Action<HoldEffect> onEffect, ILog log)
    {
        _holdVk = holdVk;
        _reducer = new HookReducer(holdVk);
        _preview = new PreviewCapture(holdVk);
        _machine = machine;
        _startingMode = startingMode;
        _log = log;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _foregroundProc = ForegroundProc;
        _thread = new Thread(Loop) { IsBackground = true, Name = "yolime-hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.Wait();
        _ = Task.Run(async () =>
        {
            await foreach (var effect in _effects.Reader.ReadAllAsync())
            {
                try { onEffect(effect); }
                catch (Exception ex) { _log.Write(LogEvent.UnhandledException, LogField.Error(ex)); }
            }
        });
    }

    /// <summary>When paused, every key passes through untouched.</summary>
    public bool Paused { get; set; }

    /// <summary>Runs <paramref name="action"/> against the machine on the hook thread, where it lives.</summary>
    public void OnMachine(Action<HoldStateMachine> action) => Post(() => action(_machine));

    /// <summary>Raised on the hook thread for each preview key or ending: the handler must only hand it off.</summary>
    public event Action<PreviewSignal>? PreviewSignaled;

    /// <summary>Raised on the hook thread for each real key press while <see cref="WatchKeys"/> is on (the Use-raw offer).</summary>
    public event Action? UserKeyPressed;

    /// <summary>Whether to report the user's key presses (only while something wants them: it costs a post per key).</summary>
    public volatile bool WatchKeys;

    /// <summary>Tells the hook whether a screen point is on the overlay card, so clicking its buttons doesn't end it.</summary>
    public Func<int, int, bool>? IsOnCard { get; set; }

    /// <summary>When the user last pressed a mouse button (not injected), on the hook's clock, while watched.</summary>
    public long LastUserMouseDownMs => Interlocked.Read(ref _lastUserMouseDownMs);

    /// <summary>
    /// Starts taking the preview card's keys, and completes once the hook has actually started: the card must not
    /// show before then, or an Enter pressed at once would reach the app (and send a chat message).
    /// </summary>
    public Task ArmPreviewAsync(int targetPid)
    {
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            _preview.Arm(Environment.TickCount64, targetPid);
            InstallMouseHook();
            armed.TrySetResult();
        });
        return armed.Task;
    }

    public void DisarmPreview() => Post(() =>
    {
        _preview.Disarm();
        UpdateMouseHook();
    });

    /// <summary>Keeps the mouse hook on to see the user's clicks (the pending paste waits for one).</summary>
    public void WatchMouse(bool on) => Post(() =>
    {
        _watchMouse = on;
        UpdateMouseHook();
    });

    /// <summary>
    /// While set (the wizard's "hold your key" step), the hold key shows feedback through <see cref="HoldKeySeen"/> and
    /// never starts a run.
    /// </summary>
    public bool SuppressRuns
    {
        get => Volatile.Read(ref _suppressRuns);
        set => Volatile.Write(ref _suppressRuns, value);
    }

    private bool _suppressRuns;

    /// <summary>
    /// While set (an update is installing), a hold that would start a run passes through untouched and raises
    /// <see cref="HoldRefused"/>. Only from idle: a hands-free recording or a run still working keeps its own keys, so
    /// it can still be stopped or cancelled. Separate from <see cref="SuppressRuns"/>, which the wizard switches off.
    /// </summary>
    public bool RefuseHolds
    {
        get => Volatile.Read(ref _refuseHolds);
        set => Volatile.Write(ref _refuseHolds, value);
    }

    private bool _refuseHolds;
    private bool _holdRefusedDown; // auto-repeat sends many key-downs: one refusal per press

    /// <summary>Raised on the hook thread for each hold-key press refused by <see cref="RefuseHolds"/>.</summary>
    public event Action? HoldRefused;

    /// <summary>Raised on the hook thread with each hold-key press and release while <see cref="SuppressRuns"/> is on.</summary>
    public event Action<bool>? HoldKeySeen;

    /// <summary>
    /// Captures the next real key pressed while a yolime window is in front (Settings' hotkey box), swallowing it.
    /// Null when the 10 s window ran out, another capture replaced this one, or yolime lost the foreground. A refused
    /// key comes back as its category only.
    /// </summary>
    public Task<CapturedKey?> CaptureKeyAsync()
    {
        var result = new TaskCompletionSource<CapturedKey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            _captured?.TrySetResult(null);
            _captured = result;
            _capture.Arm(Environment.TickCount64);
        });
        return result.Task;
    }

    public void CancelCapture() => Post(() =>
    {
        _capture.Disarm();
        _captured?.TrySetResult(null);
        _captured = null;
    });

    /// <summary>
    /// A new hold key or hold options (the latch) from Settings. Applied on the hook thread once no hold is in
    /// progress, so a hold never starts with one key and ends with another.
    /// </summary>
    public void Reconfigure(int holdVk, HoldOptions options) => Post(() =>
    {
        _reconfigure = (holdVk, options);
        ApplyReconfigureIfIdle();
    });

    private void ApplyReconfigureIfIdle()
    {
        if (_reconfigure is not { } r || _machine.State != HoldState.Idle || _machine.HoldKeyDown || _preview.Active) return;
        _reconfigure = null;
        _holdVk = r.Vk;
        _reducer = new HookReducer(r.Vk);
        _preview = new PreviewCapture(r.Vk);
        _machine = new HoldStateMachine(r.Options);
    }

    /// <summary>The session was locked, suspended or disconnected: drop any capture.</summary>
    public void SessionEnded() => Post(() => Feed(HotkeyKind.SessionEnded));

    /// <summary>Reinstalls the hooks (after unlock or resume, when Windows may have dropped them).</summary>
    public void ReArm() => Post(InstallKeyboardHook);

    private void Post(Action action)
    {
        _actions.Enqueue(action);
        User32.PostThreadMessageW(_threadId, WmRunAction, 0, 0);
    }

    private void Loop()
    {
        _threadId = User32.GetCurrentThreadId();
        User32.PeekMessageW(out _, 0, 0, 0, 0); // creates this thread's message queue before anyone posts to it
        InstallKeyboardHook();
        _foregroundHook = User32.SetWinEventHook(User32.EventSystemForeground, User32.EventSystemForeground, 0, _foregroundProc, 0, 0, User32.WinEventOutOfContext);
        // Until the first foreground event, the window in front now is the last real one (if it is real).
        if (Sample(User32.GetForegroundWindow()) is { IsReal: true } first) _lastRealPid = (uint)first.Pid;
        User32.SetTimer(0, 0, 100, 0);
        _ready.SetResult();

        while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            // A throw here would end this thread's message loop, and with it every hook: log it and carry on.
            try
            {
                if (msg.Message == WmRunAction)
                {
                    while (_actions.TryDequeue(out var action))
                    {
                        try { action(); }
                        catch (Exception ex) { _log.Write(LogEvent.UnhandledException, LogField.Error(ex)); }
                    }
                }
                else if (msg.Message == User32.WmTimer && msg.Hwnd == 0)
                {
                    OnTick();
                }
                else
                {
                    User32.TranslateMessage(msg);
                    User32.DispatchMessageW(msg);
                }
            }
            catch (Exception ex)
            {
                _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
            }
        }

        RemoveMouseHook();
        if (_keyboardHook != 0) User32.UnhookWindowsHookEx(_keyboardHook);
        if (_foregroundHook != 0) User32.UnhookWinEvent(_foregroundHook);
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != 0) User32.UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = User32.SetWindowsHookExW(User32.WhKeyboardLl, _keyboardProc, 0, 0);
        if (_keyboardHook == 0) _log.Write(LogEvent.StageFailed, LogField.Tag("stage", Core.Stage.Capture), LogField.Count("hookError", Marshal.GetLastPInvokeError()));
        _ticksSinceReArm = 0;
    }

    private void OnTick()
    {
        var now = Environment.TickCount64;
        var holding = HoldingForeground();
        // Read only when something is waiting on it: a pending switch, or the preview card.
        var front = (holding && _switch.Pending) || _preview.Active ? Sample(User32.GetForegroundWindow()) : default;
        if (_preview.Tick(now, front) is { } expired) PreviewSignaled?.Invoke(expired);
        if (holding && _switch.Pending) Settle(_switch.Tick(front, now));
        _capture.ReleaseIfQuiet(Environment.TickCount64);
        if (_capture.Tick(Environment.TickCount64))
        {
            _captured?.TrySetResult(null);
            _captured = null;
        }
        ApplyReconfigureIfIdle();
        if (_machine.State != HoldState.Idle || _machine.HoldKeyDown)
        {
            // The missed key-up watchdogs: the physical state of the hold key, independent of any hook event. Read
            // once per tick; the machine only believes a second "up" in a row (a fresh key-down can read as up).
            var down = User32.GetAsyncKeyState(_holdVk) < 0;
            Feed(HotkeyKind.Tick, down, HoldingForeground() && _switch.Settling(now));
        }
        else if (++_ticksSinceReArm >= ReArmTicks)
        {
            // Health check: Windows silently removes a hook that ever timed out, so re-arm while idle.
            InstallKeyboardHook();
        }
    }

    private nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<User32.KbdLlHookStruct>(lParam);
                var up = wParam is 0x0101 or 0x0105; // WM_KEYUP, WM_SYSKEYUP
                // Injected input never drives the card or the click watcher, except in the E2E build, whose harness
                // can only type with SendInput (the same rule HookReducer applies to the hold key).
                var injected = (k.Flags & User32.LlkhfInjected) != 0 && !HookReducer.InjectedCounts;
                // Settings' key capture comes before everything, even pause: the user is choosing a key, not dictating.
                if (_capture.Busy)
                {
                    User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out var fg);
                    var (captureTaken, captured) = _capture.Key((int)k.VkCode, up, injected || k.ExtraInfo == HookReducer.OwnInputMarker, fg == (uint)Environment.ProcessId, Environment.TickCount64);
                    // Captured, or disarmed because yolime lost the foreground: either way the waiting page hears now.
                    if (captured is not null || !_capture.Active)
                    {
                        _captured?.TrySetResult(captured is { } vk ? CapturedKey.From(vk) : null);
                        _captured = null;
                    }
                    if (captureTaken) return 1;
                }
                if (Paused) return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
                // The preview card's keys come first: a taken key never reaches the hold machine or the app.
                var (taken, signal) = _preview.Key((int)k.VkCode, up, injected, k.ExtraInfo, ShortcutModifiersDown(), Environment.TickCount64);
                if (signal is { } s)
                {
                    PreviewSignaled?.Invoke(s);
                    if (s is PreviewSignal.Expired) UpdateMouseHook();
                }
                if (taken) return 1;
                if (!up && !injected && WatchKeys && k.ExtraInfo != HookReducer.OwnInputMarker && (int)k.VkCode != _holdVk) UserKeyPressed?.Invoke();
                if (_reducer.Key((int)k.VkCode, up, (k.Flags & User32.LlkhfInjected) != 0, k.ExtraInfo) is { } kind)
                {
                    if (SuppressRuns)
                    {
                        if (kind is HotkeyKind.HoldDown or HotkeyKind.HoldUp) HoldKeySeen?.Invoke(kind == HotkeyKind.HoldDown);
                        return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
                    }
                    if (RefuseHolds && kind is HotkeyKind.HoldDown or HotkeyKind.HoldUp && _machine.State == HoldState.Idle)
                    {
                        if (kind == HotkeyKind.HoldDown && !_holdRefusedDown) HoldRefused?.Invoke();
                        _holdRefusedDown = kind == HotkeyKind.HoldDown;
                        return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
                    }
                    if (kind == HotkeyKind.HoldDown && _machine.State == HoldState.Idle)
                    {
                        // A key-down during a hidden flash belongs to the app the user was really in.
                        var front = Sample(User32.GetForegroundWindow());
                        _capturePid = front.IsReal || _lastRealPid == 0 ? (uint)front.Pid : _lastRealPid;
                        _switch.Reset((int)_capturePid);
                    }
                    if (Feed(kind)) return 1;
                }
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a hook callback would take the process down.
            _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
        }
        return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private nint MouseProc(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0 && !Paused)
            {
                var m = Marshal.PtrToStructure<User32.MsLlHookStruct>(lParam);
                var isDown = wParam is 0x0201 or 0x0204 or 0x0207; // left, right, middle button down
                if (isDown && ((m.Flags & User32.LlmhfInjected) == 0 || HookReducer.InjectedCounts) && m.ExtraInfo != HookReducer.OwnInputMarker)
                {
                    // Left clicks only: a right-click opens a menu, and pasting then would go wherever the menu sends it.
                    if (_watchMouse && wParam == 0x0201) Interlocked.Exchange(ref _lastUserMouseDownMs, Environment.TickCount64);
                    if (_preview.MouseDown(IsOnCard?.Invoke(m.X, m.Y) ?? false) is { } ended)
                    {
                        PreviewSignaled?.Invoke(ended);
                        UpdateMouseHook();
                    }
                }
                if (m.ExtraInfo != HookReducer.OwnInputMarker
                    && _reducer.Mouse((int)wParam, (m.Flags & User32.LlmhfInjected) != 0) is { } kind)
                {
                    Feed(kind);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
        }
        return User32.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    /// <summary>Every foreground change, raised on the hook thread: the handler must only hand the work off.</summary>
    public event Action<nint>? ForegroundSwitched;

    private void ForegroundProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // Its own guard: a throwing subscriber mustn't stop the foreground rules below for this event.
            try { ForegroundSwitched?.Invoke(hwnd); }
            catch (Exception ex) { _log.Write(LogEvent.UnhandledException, LogField.Error(ex)); }
            var window = Sample(hwnd);
            if (window.IsReal) _lastRealPid = (uint)window.Pid;
            var now = Environment.TickCount64;
            if (_preview.Active && _preview.Foreground(window, now) is { } ended) PreviewSignaled?.Invoke(ended);
            // Within the same app a new foreground window (a menu, a dialog) isn't a switch away; another app's starts
            // a pending switch that the ticks decide (ForegroundRule).
            if (HoldingForeground()) Settle(_switch.Observe(window, now));
        }
        catch (Exception ex)
        {
            _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
        }
    }

    /// <returns>Whether to swallow the event.</returns>
    private bool HoldingForeground() => _machine.State is HoldState.Pressed or HoldState.Recording or HoldState.Latched;

    private void Settle(ForegroundResult result)
    {
        if (result.Verdict == ForegroundVerdict.Switched) Feed(HotkeyKind.ForegroundChanged);
        else if (result.Verdict == ForegroundVerdict.Settled) _log.Write(LogEvent.ForegroundSettled, LogField.Ms("ms", result.PendingMs));
    }

    /// <summary>What the foreground rules need to know about a window: its process, and whether anyone can see it.</summary>
    internal static ForegroundSample Sample(nint hwnd)
    {
        if (hwnd == 0) return default;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        return new((int)pid, User32.IsWindowVisible(hwnd), User32.IsCloaked(hwnd), ForegroundSample.IsShellTrayClass(User32.ClassName(hwnd)));
    }

    private bool Feed(HotkeyKind kind, bool holdDown = true, bool switchPending = false)
    {
        // At a new press, Shift's real state: resyncs a Shift up that went to an admin window.
        bool? shift = kind == HotkeyKind.HoldDown ? User32.GetAsyncKeyState(0x10) < 0 : null; // VK_SHIFT
        var (swallow, effects) = _machine.Handle(new HotkeyInput(kind, Environment.TickCount64, holdDown, switchPending, shift), _startingMode());
        foreach (var e in effects) _effects.Writer.TryWrite(e);
        UpdateMouseHook();
        ApplyReconfigureIfIdle();
        return swallow;
    }

    /// <summary>
    /// A mouse hook adds latency to every mouse event system-wide, so it's only on while a hold can be aborted, a
    /// preview card is up, or a pending paste waits for a click.
    /// </summary>
    private void UpdateMouseHook()
    {
        if (_machine.State is HoldState.Pressed or HoldState.Recording || _preview.Active || _watchMouse) InstallMouseHook();
        else RemoveMouseHook();
    }

    /// <summary>Ctrl, Alt or a Windows key held: a shortcut, which ends a preview rather than answering it.</summary>
    private static bool ShortcutModifiersDown() =>
        User32.GetAsyncKeyState(0x11) < 0 || User32.GetAsyncKeyState(0x12) < 0 || User32.GetAsyncKeyState(0x5B) < 0 || User32.GetAsyncKeyState(0x5C) < 0;

    private void InstallMouseHook()
    {
        if (_mouseHook == 0) _mouseHook = User32.SetWindowsHookExW(User32.WhMouseLl, _mouseProc, 0, 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == 0) return;
        User32.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = 0;
    }

    public void Dispose()
    {
        User32.PostThreadMessageW(_threadId, User32.WmQuit, 0, 0);
        _effects.Writer.TryComplete();
    }
}
