using System.Runtime.InteropServices;

namespace Yolime.E2E;

/// <summary>Finds and samples yolime's overlay window from outside, with physical (DPI-aware) coordinates.</summary>
internal static partial class Overlay
{
    public static nint Find() => FindWindowW("yolime-overlay", null);

    public static nint FindPill() => FindWindowW("yolime-overlay-pill", null);

    public static bool Visible(nint hwnd) => IsWindowVisible(hwnd);

    // The harness process isn't DPI-aware (WinForms' csproj setting needs ApplicationConfiguration.Initialize), so a
    // plain GetWindowRect returns scaled coordinates while GetPixel reads physical ones. Both run per-monitor-aware here.
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>A point just inside the card's left edge, mid-height: fill, not text or border.</summary>
    public static (int X, int Y) InsideLeftEdge(nint hwnd)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            GetWindowRect(hwnd, out var r);
            return (r.Left + 8, (r.Top + r.Bottom) / 2);
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    /// <summary>The window's rect in physical pixels.</summary>
    public static (int Left, int Top, int Right, int Bottom) WindowRect(nint hwnd)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            GetWindowRect(hwnd, out var r);
            return (r.Left, r.Top, r.Right, r.Bottom);
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    public static (int X, int Y) Centre(nint hwnd)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            GetWindowRect(hwnd, out var r);
            return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    /// <summary>The screen pixel at a physical point, as COLORREF (0x00BBGGRR).</summary>
    public static uint Pixel(int x, int y)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        var dc = GetDC(0);
        try { return GetPixel(dc, x, y); }
        finally
        {
            ReleaseDC(0, dc);
            SetThreadDpiAwarenessContext(old);
        }
    }

    /// <summary>One capture of a physical-pixel rect, read in memory (per-pixel GetPixel calls on the screen are slow).</summary>
    public static Bitmap Grab(int left, int top, int width, int height)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(left, top, 0, 0, new Size(width, height));
            return bmp;
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    /// <summary>Clicks a physical point (matching <see cref="Centre"/>), whatever the harness's own DPI awareness.</summary>
    public static void ClickPhysical(int x, int y)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try { Input.Click(x, y); }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string? cls, string? title);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect r);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial uint GetPixel(nint dc, int x, int y);
}
