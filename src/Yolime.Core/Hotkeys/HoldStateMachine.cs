namespace Yolime.Core.Hotkeys;

/// <summary>What the hook reports. Key codes never get this far: the hook reduces every event to one of these.</summary>
public enum HotkeyKind { HoldDown, HoldUp, ShiftDown, ShiftUp, Esc, OtherKey, Mouse, ForegroundChanged, SessionEnded, Tick }

/// <param name="AtMs">A monotonic millisecond clock.</param>
/// <param name="HoldPhysicallyDown">For <see cref="HotkeyKind.Tick"/>: whether the hold key is physically down now.</param>
/// <param name="SwitchPending">For <see cref="HotkeyKind.Tick"/>: a foreground switch away is waiting to settle.</param>
/// <param name="ShiftPhysicallyDown">For <see cref="HotkeyKind.HoldDown"/>: whether Shift is physically down (null: unknown).</param>
public readonly record struct HotkeyInput(HotkeyKind Kind, long AtMs, bool HoldPhysicallyDown = true, bool SwitchPending = false, bool? ShiftPhysicallyDown = null);

public enum HoldState { Idle, Pressed, Recording, Latched, Processing, ProcessingPressed }

public enum CommitReason { Released, MissedKeyUp, Cap, LatchEnded, ForegroundChanged }

public enum DiscardReason { ShortTap, Esc, OtherInput, ForegroundChanged, SessionEnded }

public abstract record HoldEffect;

/// <summary>The key went down: start listening now, so nothing said before the 300 ms mark is lost.</summary>
public sealed record StartCapture(Mode Mode) : HoldEffect;

/// <summary>The hold passed 300 ms: it's a dictation, not a shortcut.</summary>
public sealed record ShowRecording : HoldEffect;

public sealed record CycleMode(Mode Next) : HoldEffect;

/// <param name="ShiftHeld">
/// A Shift pressed during the recording was still held at release, for <see cref="HoldOptions.PreviewShiftMs"/> or
/// more: show the preview card. Only for a normal release.
/// </param>
public sealed record Commit(CommitReason Reason, bool ShiftHeld = false) : HoldEffect;

/// <param name="FromWatchdog">The key-up was inferred from the key's state at a tick, not seen by the hook.</param>
public sealed record Discard(DiscardReason Reason, bool FromWatchdog = false) : HoldEffect;

public sealed record CancelProcessing : HoldEffect;

public sealed record RejectBusy : HoldEffect;

public sealed record CapWarning(int SecondsLeft) : HoldEffect;

public sealed record Latched : HoldEffect;

public sealed record HoldOptions(
    bool DoubleTapLatch = false,
    bool ModeCycling = true,
    int TapMs = 300,
    int DoubleTapWindowMs = 400,
    int CancelDebounceMs = 400,
    int HeldCapMs = 120_000,
    int LatchedCapMs = 300_000,
    int CapWarningMs = 10_000,
    int PreviewShiftMs = 250);

/// <summary>
/// The hold-key rules from the spec, as a pure function of key events and time. It runs synchronously inside the
/// keyboard hook (whether to swallow a key must be decided there), so it is single-threaded and allocates little.
/// </summary>
/// <remarks>
/// Two things make it safe around ordinary Right-Ctrl use. It tracks the hold key's physical state, so auto-repeat
/// never counts as a new press in any state (a release after the cap auto-commit can't cancel the run). And under
/// 300 ms any other input means the user is making a shortcut, so Shift and Esc pass through and the capture is
/// dropped.
/// </remarks>
public sealed class HoldStateMachine(HoldOptions options)
{
    private static readonly IReadOnlyList<HoldEffect> None = [];

    private bool _holdDown;
    private int _upReadings;
    private bool _shiftDown;
    private bool _shiftSwallowed;
    // A Shift pressed during this recording (its down was swallowed), and when: its up cycles the mode, or, still held
    // long enough at release, it asks for a preview.
    private bool _shiftInRecording;
    private long _shiftDownAt;
    private long _pressAt;
    private long _latchAt;
    private long _commitAt = long.MinValue;
    private long? _lastTapUpAt;
    private bool _latchKeyDown;
    private bool _endingLatch;
    private bool _busyRejected;
    private bool _pressVoided;
    private int _lastWarning;
    private Mode _mode;

    public HoldState State { get; private set; } = HoldState.Idle;

    /// <summary>
    /// Whether the machine believes the hold key is down. The hook's watchdog compares this with the key's physical
    /// state in every state, and reports a key-up it missed (one that went to an admin window, for instance); without
    /// that, the next real press would be taken for auto-repeat and silently ignored.
    /// </summary>
    public bool HoldKeyDown => _holdDown;

    public (bool Swallow, IReadOnlyList<HoldEffect> Effects) Handle(HotkeyInput input, Mode startingMode)
    {
        switch (input.Kind)
        {
            case HotkeyKind.HoldDown:
                if (_holdDown) return (false, None); // auto-repeat, in every state
                _holdDown = true;
                _upReadings = 0;
                // Only for a Shift the system saw go down: one we swallowed never reaches the async key state, so it
                // reads as up while the user still holds it, and clearing it would let its up through unpaired.
                if (input.ShiftPhysicallyDown == false && !_shiftSwallowed)
                {
                    // A Shift up lost to an admin window: otherwise every later Shift would look like auto-repeat.
                    _shiftDown = false;
                    _shiftSwallowed = false;
                }
                return (false, HoldDown(input.AtMs, startingMode));
            case HotkeyKind.HoldUp:
                if (!_holdDown) return (false, None);
                _holdDown = false;
                return (false, HoldUp(input.AtMs));
            case HotkeyKind.ShiftDown:
                return ShiftDown(input.AtMs);
            case HotkeyKind.ShiftUp:
            {
                // Swallow an up only if we swallowed its down: otherwise Windows would think Shift is still held.
                _shiftDown = false;
                var swallow = _shiftSwallowed;
                _shiftSwallowed = false;
                // The mode changes on the up: a Shift still held at release is a preview of this mode, not a cycle.
                var cycle = _shiftInRecording && State == HoldState.Recording && options.ModeCycling;
                _shiftInRecording = false;
                return (swallow, cycle ? [new CycleMode(_mode = Next(_mode))] : None);
            }
            case HotkeyKind.Esc:
                return Esc();
            case HotkeyKind.OtherKey or HotkeyKind.Mouse:
                return (false, OtherInput());
            case HotkeyKind.ForegroundChanged:
                return (false, State switch
                {
                    HoldState.Pressed or HoldState.Recording => DiscardWith(DiscardReason.ForegroundChanged),
                    // Hands-free may have run for minutes: keep it (the target check then puts it on the clipboard).
                    HoldState.Latched => CommitWith(CommitReason.ForegroundChanged, input.AtMs),
                    _ => None,
                });
            case HotkeyKind.SessionEnded:
                // Locked, suspended or disconnected: never keep recording a room nobody is in.
                return (false, State is HoldState.Pressed or HoldState.Recording or HoldState.Latched ? DiscardWith(DiscardReason.SessionEnded) : None);
            case HotkeyKind.Tick:
                return (false, Watchdog(input));
            default:
                return (false, None);
        }
    }

    /// <summary>The pipeline finished or was cancelled: ready for the next hold.</summary>
    public void SetProcessing(bool processing)
    {
        if (!processing && State is HoldState.Processing or HoldState.ProcessingPressed) State = HoldState.Idle;
    }

    /// <summary>The run behind this hold ended early (no key, mic denied): back to Idle, whatever the state.</summary>
    public void Abort()
    {
        State = HoldState.Idle;
        _shiftInRecording = false;
    }

    private IReadOnlyList<HoldEffect> HoldDown(long at, Mode startingMode)
    {
        switch (State)
        {
            case HoldState.Idle:
                _mode = startingMode;
                _lastWarning = 0;
                if (options.DoubleTapLatch && _lastTapUpAt is { } tap && at - tap <= options.DoubleTapWindowMs)
                {
                    State = HoldState.Latched;
                    _latchAt = at;
                    _latchKeyDown = true;
                    _endingLatch = false;
                    _lastTapUpAt = null;
                    return [new StartCapture(_mode), new Latched()];
                }
                State = HoldState.Pressed;
                _pressAt = at;
                return [new StartCapture(_mode)];
            case HoldState.Latched:
                _latchKeyDown = true;
                _endingLatch = true;
                return None;
            case HoldState.Processing:
                State = HoldState.ProcessingPressed;
                _pressAt = at;
                _busyRejected = false;
                // Right after a commit, a press is a bounce or a nervous re-tap, not a cancel.
                _pressVoided = at - _commitAt < options.CancelDebounceMs;
                return None;
            default:
                return None;
        }
    }

    private IReadOnlyList<HoldEffect> HoldUp(long at)
    {
        switch (State)
        {
            case HoldState.Pressed when at - _pressAt < options.TapMs:
                _lastTapUpAt = at;
                return DiscardWith(DiscardReason.ShortTap);
            case HoldState.Pressed or HoldState.Recording:
                return CommitWith(CommitReason.Released, at);
            case HoldState.Latched when _endingLatch:
                return CommitWith(CommitReason.LatchEnded, at);
            case HoldState.Latched:
                // Releasing the press that started the latch: recording carries on hands-free.
                _latchKeyDown = false;
                return None;
            case HoldState.ProcessingPressed:
                State = HoldState.Processing;
                return !_pressVoided && at - _pressAt < options.TapMs ? [new CancelProcessing()] : None;
            default:
                return None;
        }
    }

    private (bool, IReadOnlyList<HoldEffect>) ShiftDown(long at)
    {
        if (_shiftDown) return (_shiftSwallowed, None); // auto-repeat: same answer as the first down
        _shiftDown = true;
        switch (State)
        {
            case HoldState.Pressed:
                // Under 300 ms Right Ctrl + Shift is a shortcut (select word, reopen tab): let it through.
                return (false, DiscardWith(DiscardReason.OtherInput));
            case HoldState.Recording:
                // Swallowed even when cycling is off: Ctrl+Shift is a common keyboard-layout switch.
                _shiftSwallowed = true;
                _shiftInRecording = true;
                _shiftDownAt = at;
                return (true, None);
            case HoldState.ProcessingPressed:
                _pressVoided = true;
                return (false, None);
            default:
                return (false, None);
        }
    }

    private (bool, IReadOnlyList<HoldEffect>) Esc() => State switch
    {
        HoldState.Pressed => (false, DiscardWith(DiscardReason.OtherInput)),
        HoldState.Recording => (true, DiscardWith(DiscardReason.Esc)),
        // Hands-free: Esc belongs to whatever the user is doing, unless they're holding the key to cancel.
        HoldState.Latched when _latchKeyDown && _holdDown => (true, DiscardWith(DiscardReason.Esc)),
        _ => (false, None),
    };

    private IReadOnlyList<HoldEffect> OtherInput()
    {
        switch (State)
        {
            case HoldState.Pressed or HoldState.Recording:
                return DiscardWith(DiscardReason.OtherInput);
            case HoldState.ProcessingPressed:
                // Right Ctrl was used for a shortcut while waiting: that press is not a cancel tap.
                _pressVoided = true;
                return None;
            default:
                return None;
        }
    }

    /// <summary>
    /// The missed key-up watchdog. Right after the hook sees a key-down, the key can still read as up for about a
    /// millisecond (a Sandbox probe saw it on 88 of 200 key-downs), and a tick landing there would turn a hold into a
    /// tap. So only a second "up" reading in a row counts; a "down" reading or a new press starts the count again.
    /// </summary>
    private IReadOnlyList<HoldEffect> Watchdog(HotkeyInput input)
    {
        var up = false;
        if (input.HoldPhysicallyDown) _upReadings = 0;
        else up = ++_upReadings >= 2;
        if (!up) return Tick(input.AtMs, holdDown: true, input.SwitchPending);
        if (State is HoldState.Pressed or HoldState.Recording)
        {
            _holdDown = false;
            return Tick(input.AtMs, holdDown: false, input.SwitchPending);
        }
        // The key-up went somewhere the hook couldn't see (an admin window): report it, or the next real press would
        // look like auto-repeat and be ignored.
        if (_holdDown)
        {
            _holdDown = false;
            return HoldUp(input.AtMs);
        }
        return Tick(input.AtMs, holdDown: false, input.SwitchPending);
    }

    private IReadOnlyList<HoldEffect> Tick(long at, bool holdDown, bool switchPending)
    {
        switch (State)
        {
            case HoldState.Pressed when !holdDown:
                return at - _pressAt < options.TapMs ? DiscardWith(DiscardReason.ShortTap, fromWatchdog: true) : CommitWith(CommitReason.MissedKeyUp, at);
            // While a switch away is settling, stay quiet: if it discards, nothing should have announced a recording.
            case HoldState.Pressed when at - _pressAt >= options.TapMs && !switchPending:
                State = HoldState.Recording;
                return [new ShowRecording(), .. Cap(at, at - _pressAt, options.HeldCapMs)];
            case HoldState.Recording when !holdDown:
                return CommitWith(CommitReason.MissedKeyUp, at);
            case HoldState.Recording:
                return Cap(at, at - _pressAt, options.HeldCapMs);
            case HoldState.Latched:
                return Cap(at, at - _latchAt, options.LatchedCapMs);
            case HoldState.ProcessingPressed when !_busyRejected && !_pressVoided && at - _pressAt >= options.TapMs:
                _busyRejected = true;
                return [new RejectBusy()];
            default:
                return None;
        }
    }

    /// <summary>Commits at the cap (never drops), with a once-a-second countdown in the last 10 s.</summary>
    private IReadOnlyList<HoldEffect> Cap(long at, long elapsed, int cap)
    {
        var remaining = cap - elapsed;
        if (remaining <= 0) return CommitWith(CommitReason.Cap, at);
        if (remaining > options.CapWarningMs) return None;
        var seconds = (int)Math.Ceiling(remaining / 1000.0);
        if (seconds == _lastWarning) return None;
        _lastWarning = seconds;
        return [new CapWarning(seconds)];
    }

    private IReadOnlyList<HoldEffect> CommitWith(CommitReason reason, long at)
    {
        State = HoldState.Processing;
        _commitAt = at;
        var shift = _shiftInRecording && _shiftDown;
        _shiftInRecording = false;
        // Only a normal release previews: at the cap, a hands-free end or a switch away, Shift may be held for typing.
        if (reason != CommitReason.Released || !shift) return [new Commit(reason)];
        if (at - _shiftDownAt >= options.PreviewShiftMs) return [new Commit(reason, ShiftHeld: true)];
        // Let go together with the hold key: a mode tap that rolled over the release, so it still changes the mode.
        return options.ModeCycling ? [new CycleMode(_mode = Next(_mode)), new Commit(reason)] : [new Commit(reason)];
    }

    private IReadOnlyList<HoldEffect> DiscardWith(DiscardReason reason, bool fromWatchdog = false)
    {
        State = HoldState.Idle;
        _shiftInRecording = false;
        return [new Discard(reason, fromWatchdog)];
    }

    private static Mode Next(Mode m) => m switch
    {
        Mode.Dictate => Mode.Prompt,
        Mode.Prompt => Mode.Reply,
        Mode.Reply => Mode.Raw,
        _ => Mode.Dictate,
    };
}
