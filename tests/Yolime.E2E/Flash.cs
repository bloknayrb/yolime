using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Yolime.E2E;

/// <summary>
/// Another process taking the foreground mid-hold, the way background helpers do: the harness starts itself with
/// <c>--flash</c>, and this child puts its own window in front for a while, then hands the foreground back to the form.
/// A hidden window reproduces the Sandbox probe's rgnupdt.exe flash; a visible one is a real switch away.
/// </summary>
internal static partial class Flash
{
    private const int AsfwAny = -1;

    /// <summary>Harness side. Returns null if the child's window took the foreground, else what happened.</summary>
    public static string? Run(nint formHwnd, bool visible, int holdMs)
    {
        AllowSetForegroundWindow(AsfwAny);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var a in new[] { "--flash", formHwnd.ToString(), visible ? "visible" : "hidden", holdMs.ToString() }) start.ArgumentList.Add(a);
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(15_000)) return "the flash process never exited";
        return child.ExitCode == 0 ? null : $"the flash window never took the foreground (exit {child.ExitCode})";
    }

    /// <summary>Child side: exit 0 if its window was in front, 2 if it never got there.</summary>
    public static int Child(string[] args)
    {
        var form = (nint)long.Parse(args[1]);
        var visible = args[2] == "visible";
        var holdMs = int.Parse(args[3]);
        using var window = new Form { Text = "flash", ShowInTaskbar = false, Width = 300, Height = 200 };
        var hwnd = window.Handle; // created, not shown
        if (visible) window.Show();
        SetForegroundWindow(hwnd);
        var took = Program.Wait(() => GetForegroundWindow() == hwnd, 2000);
        var end = Environment.TickCount64 + holdMs;
        while (Environment.TickCount64 < end)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        SetForegroundWindow(form);
        Program.Wait(() => GetForegroundWindow() == form, 2000);
        return took ? 0 : 2;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
