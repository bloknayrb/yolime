using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FlaUI.UIA3;
using Yolime.Core.Output;

namespace Yolime.App.Native;

/// <summary>
/// Reads where text would go: the foreground window and process, whether UIPI would block input to it, and (through
/// UI Automation) the focused element's identity, class, password flag and editability. It never reads the
/// element's value, name or text. UIA runs on a dedicated MTA thread with a 300 ms budget; a thread stuck in a hung
/// provider is abandoned and replaced.
/// </summary>
internal sealed class TargetReader : ITargetReader, IDisposable
{
    private static readonly TimeSpan UiaBudget = TimeSpan.FromMilliseconds(300);
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int GwlStyle = -16;
    private const long EsPassword = 0x0020;

    private UiaWorker _worker = new();
    private readonly int _ownIntegrity = IntegrityOf(User32.GetCurrentProcess());

    /// <summary>Whether yolime itself runs above medium integrity (admin), which the tray warns about.</summary>
    public bool SelfElevated => _ownIntegrity > 0x2000;

    /// <summary>Whether UIPI would block our input into this window (its process runs at a higher integrity).</summary>
    public bool BlocksInput(nint hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        return ProcessInfo(pid).Integrity > _ownIntegrity;
    }

    private static readonly uint OwnPid = (uint)Environment.ProcessId;
    private static nint _practiceWindow;

    /// <summary>
    /// The wizard's practice window, the one yolime window dictation may paste into (0 when it isn't open). Every other
    /// yolime window is read without UIA (it would query our own UI thread) and marked as our own, so nothing is
    /// pasted there.
    /// </summary>
    public static nint PracticeWindow
    {
        get => Volatile.Read(ref _practiceWindow);
        set => Volatile.Write(ref _practiceWindow, value);
    }

    public nint Foreground() => User32.GetForegroundWindow();

    public TargetSnapshot Snapshot()
    {
        var hwnd = User32.GetForegroundWindow();
        var thread = User32.GetWindowThreadProcessId(hwnd, out var pid);
        var (name, integrity) = ProcessInfo(pid);
        if (pid == OwnPid && (hwnd == 0 || hwnd != PracticeWindow))
            return new TargetSnapshot(hwnd, (int)pid, name, User32.ClassName(hwnd), null, null, false, null, false, IsOwnProcess: true);
        var focus = FocusInfo();
        var password = focus.IsPassword || Win32PasswordFocus(thread);
        // UIPI blocks input only into a process at a higher integrity level than ours.
        var blocked = integrity > _ownIntegrity;
        return new TargetSnapshot(hwnd, (int)pid, name, User32.ClassName(hwnd), focus.RuntimeId, focus.Class, password, focus.IsEditable, blocked);
    }

    public Task WaitForIdleAsync(nint hwnd, CancellationToken ct) => Task.Run(() =>
    {
        // Best effort: WM_NULL returns once the target's thread is responsive, which usually but not provably means it
        // has handled our Ctrl+V (sent messages can overtake queued input). The fixed 800 ms after it is the real
        // margin. Apps that read the clipboard lazily (RDP, VMs) can still race it; a later PR may move to delayed rendering.
        const uint SmtoAbortIfHung = 0x0002;
        if (!User32.IsHungAppWindow(hwnd)) User32.SendMessageTimeoutW(hwnd, 0, 0, 0, SmtoAbortIfHung, 2000, out _);
    }, ct);

    /// <summary>
    /// The window and process only, without UIA: cheap enough to run on every foreground change, for picking a
    /// hold's starting mode. Focus fields are null, so only process- and class-based terminals are recognised.
    /// </summary>
    public static TargetSnapshot Light(nint hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        var (name, _) = ProcessInfo(pid);
        return new TargetSnapshot(hwnd, (int)pid, name, User32.ClassName(hwnd), null, null, false, null, false);
    }

    /// <summary>Warms UIA (the first query into an app is the slow one).</summary>
    public void Warm() => _ = FocusInfo();

    private (string? RuntimeId, string? Class, bool IsPassword, bool? IsEditable) FocusInfo()
    {
        var job = _worker.Run();
        if (job.Wait(UiaBudget)) return job.Result;
        // The provider is hung: abandon this worker, so the next snapshot doesn't queue behind it.
        _worker.Abandon();
        _worker = new UiaWorker();
        return (null, null, false, null);
    }

    /// <summary>Classic Win32 edit controls flag password boxes with ES_PASSWORD, which works even when UIA doesn't.</summary>
    private static bool Win32PasswordFocus(uint thread)
    {
        var info = new User32.GuiThreadInfo { Size = Marshal.SizeOf<User32.GuiThreadInfo>() };
        if (!User32.GetGUIThreadInfo(thread, ref info) || info.Focus == 0) return false;
        return (User32.GetWindowLongPtrW(info.Focus, GwlStyle) & EsPassword) != 0
            && User32.ClassName(info.Focus).Contains("Edit", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Name, int Integrity) ProcessInfo(uint pid)
    {
        var process = User32.OpenProcess(ProcessQueryLimitedInformation, false, pid);
        // Can't even query it: protected or higher integrity. Treat as blocked.
        if (process == 0) return ("", int.MaxValue);
        try
        {
            var buf = new char[1024];
            var size = buf.Length;
            var name = User32.QueryFullProcessImageNameW(process, 0, buf, ref size) ? Path.GetFileNameWithoutExtension(new string(buf, 0, size)) : "";
            return (name, IntegrityOf(process));
        }
        finally { User32.CloseHandle(process); }
    }

    /// <summary>The process's mandatory integrity RID (0x2000 medium, 0x3000 high); int.MaxValue if unreadable.</summary>
    private static int IntegrityOf(nint process)
    {
        if (!User32.OpenProcessToken(process, TokenQuery, out var token)) return int.MaxValue;
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!User32.GetTokenInformation(token, TokenIntegrityLevel, buffer, 256, out _)) return int.MaxValue;
            var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
            var count = Marshal.ReadByte(User32.GetSidSubAuthorityCount(sid));
            return Marshal.ReadInt32(User32.GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            User32.CloseHandle(token);
        }
    }

    public void Dispose() => _worker.Abandon();

    private sealed class UiaWorker
    {
        private readonly BlockingCollection<TaskCompletionSource<(string?, string?, bool, bool?)>> _jobs = new();

        public UiaWorker()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "yolime-uia" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public Task<(string?, string?, bool, bool?)> Run()
        {
            var job = new TaskCompletionSource<(string?, string?, bool, bool?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_jobs.IsAddingCompleted) _jobs.Add(job);
            else job.SetResult((null, null, false, null));
            return job.Task;
        }

        public void Abandon() => _jobs.CompleteAdding();

        private void Loop()
        {
            using var automation = new UIA3Automation { ConnectionTimeout = UiaBudget, TransactionTimeout = UiaBudget };
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                try
                {
                    var el = automation.FocusedElement();
                    var props = el.Properties;
                    var runtimeId = props.RuntimeId.TryGetValue(out var rid) ? string.Join('.', rid) : null;
                    var cls = props.ClassName.TryGetValue(out var c) ? c : null;
                    var password = props.IsPassword.TryGetValue(out var p) && p;
                    // Positive evidence only: no ValuePattern (Word, Chromium, terminals) means "unknown", which pastes.
                    bool? editable = el.Patterns.Value.TryGetPattern(out var value) && value.IsReadOnly.TryGetValue(out var ro) && ro ? false : null;
                    job.TrySetResult((runtimeId, cls, password, editable));
                }
                catch (Exception)
                {
                    job.TrySetResult((null, null, false, null));
                }
            }
        }
    }
}
