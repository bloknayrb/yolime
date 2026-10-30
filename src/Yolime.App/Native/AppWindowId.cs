using System.Runtime.InteropServices;

namespace Yolime.App.Native;

/// <summary>
/// A window as History's Re-insert targets it: the handle plus its process id and the process's start time, so a
/// handle Windows has since given to another window (or a recycled process id) is caught rather than pasted into.
/// </summary>
internal readonly partial record struct AppWindowId(nint Hwnd, uint Pid, long ProcessStart)
{
    public static AppWindowId? Of(nint hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        var start = StartOf(pid);
        return start == 0 ? null : new AppWindowId(hwnd, pid, start);
    }

    /// <summary>Still the same window of the same running process.</summary>
    public bool StillValid()
    {
        if (!IsWindow(Hwnd)) return false;
        User32.GetWindowThreadProcessId(Hwnd, out var pid);
        return pid == Pid && StartOf(pid) == ProcessStart;
    }

    public bool IsMinimised => IsIconic(Hwnd);

    public void Restore() => ShowWindow(Hwnd, 9); // SW_RESTORE

    private static long StartOf(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return 0;
        try
        {
            return GetProcessTimes(process, out var created, out _, out _, out _) ? created : 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);
}
