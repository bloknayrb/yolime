using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Yolime.App.Shell;

/// <summary>
/// An app-owned Win32 popup that never activates, hosting a XAML island. The overlay spike showed a WinUI Window
/// shown without activation sometimes never draws, so yolime owns the HWND and the island renders into it (proven in
/// the Sandbox: drawn, and the target kept focus).
/// </summary>
/// <remarks>
/// WS_EX_NOACTIVATE plus MA_NOACTIVATE keep focus in the user's app even when the window is clicked. WS_EX_LAYERED
/// stays on for good (toggling it can blank composited content); only WS_EX_TRANSPARENT toggles, for click-through.
/// All calls on the UI thread.
/// </remarks>
internal sealed partial class OverlayWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExLayered = 0x00080000;
    private const int GwlExStyle = -20;
    private const uint WmMouseActivate = 0x0021;
    private const nint MaNoActivate = 3;
    private const int SwShowNoActivate = 4;
    private const int SwHide = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint LwaAlpha = 0x2;
    private const uint WdaNone = 0, WdaExcludeFromCapture = 0x11;

    private readonly WndProc _wndProc;
    private readonly DesktopWindowXamlSource _island;
    private readonly FrameworkElement _content;
    private bool _clickThrough = true;
    private bool _excludeFromCapture;

    public OverlayWindow(string className, FrameworkElement content, bool clickThrough)
    {
        _content = content;
        _wndProc = OnMessage;
        var cls = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = GetModuleHandleW(null),
            ClassName = className,
        };
        RegisterClassExW(ref cls);
        _clickThrough = clickThrough;
        var ex = WsExNoActivate | WsExTopmost | WsExToolWindow | WsExLayered | (clickThrough ? WsExTransparent : 0);
        Hwnd = CreateWindowExW(ex, className, "yolime", WsPopup, 0, 0, 1, 1, 0, 0, cls.Instance, 0);
        // Layered at full alpha: needed for WS_EX_TRANSPARENT to let clicks fall through, and otherwise invisible.
        SetLayeredWindowAttributes(Hwnd, 0, 255, LwaAlpha);

        _island = new DesktopWindowXamlSource();
        _island.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(Hwnd));
        _island.Content = content;
    }

    public nint Hwnd { get; }

#if YOLIME_E2E
    /// <summary>E2E only: makes every card this many pixels too short, so the fit check can be seen failing.</summary>
    internal static int TestShrinkPx;
#endif

    public bool Visible { get; private set; }

    private static void InvalidateTree(UIElement element)
    {
        element.InvalidateMeasure();
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i) is UIElement child) InvalidateTree(child);
    }

    /// <summary>The window's rect in physical pixels, for hit-testing clicks against it.</summary>
    public (int Left, int Top, int Right, int Bottom) Bounds { get; private set; }

    public void SetClickThrough(bool on)
    {
        if (_clickThrough == on) return;
        _clickThrough = on;
        var ex = (uint)GetWindowLongPtrW(Hwnd, GwlExStyle);
        ex = on ? ex | WsExTransparent : ex & ~WsExTransparent;
        SetWindowLongPtrW(Hwnd, GwlExStyle, (nint)ex);
        if (_excludeFromCapture) SetWindowDisplayAffinity(Hwnd, WdaExcludeFromCapture);
    }

    /// <summary>Keeps the window out of screenshots and screen sharing. False if Windows refused.</summary>
    public bool SetExcludeFromCapture(bool on)
    {
        _excludeFromCapture = on;
        return SetWindowDisplayAffinity(Hwnd, on ? WdaExcludeFromCapture : WdaNone);
    }

    /// <summary>
    /// Sizes the window to its content (measured at up to <paramref name="maxWidthDip"/>) at the DPI of the monitor the
    /// user is working on, and shows it at the top or bottom centre of that monitor's work area, or beside
    /// <paramref name="besideRight"/> when given.
    /// </summary>
    public void Show(bool bottom, double maxWidthDip, (int Left, int Top, int Right, int Bottom)? besideRight = null)
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfoW(monitor, ref info);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var scale = dpi / 96.0;

        // On a monitor change, move there first (still hidden) so the island lays out at that monitor's scale.
        // (Compared with where the window really is, not a remembered monitor: a visible card can move monitors too.)
        if (!Visible && monitor != MonitorFromWindow(Hwnd, 2))
            SetWindowPos(Hwnd, 0, info.Work.Left, info.Work.Top, 0, 0, SwpNoActivate | SwpNoSize | SwpNoZOrder);
        // A short card shown right after a long one kept the long one's full width: drop every cached measure first.
        InvalidateTree(_content);
        _content.Measure(new Windows.Foundation.Size(maxWidthDip, double.PositiveInfinity));
        // 1 DIP of slack: the island's layout rounding can leave the measured width a fraction short of the text.
        var w = (int)Math.Ceiling((_content.DesiredSize.Width + 1) * scale);
        // The island lays the content out again at the window's own width, where text can wrap onto more lines than
        // it did at the 560 DIP measure (a card's buttons were cut off below the edge). Measure again at that width so
        // the height is the one the island will need.
        _content.Measure(new Windows.Foundation.Size(w / scale, double.PositiveInfinity));
        // And 1 DIP in height: the island rounds each line and button to whole pixels, which added up to ~0.6 DIP
        // more than the measure at 125%.
        var h = (int)Math.Ceiling((_content.DesiredSize.Height + 1) * scale);
#if YOLIME_E2E
        h = Math.Max(1, h - TestShrinkPx);
#endif
        int x, y;
        if (besideRight is { } r)
        {
            (x, y) = (r.Right + (int)(8 * scale), r.Top + ((r.Bottom - r.Top) - h) / 2);
        }
        else
        {
            x = info.Work.Left + ((info.Work.Right - info.Work.Left) - w) / 2;
            y = bottom ? info.Work.Bottom - h - (int)(16 * scale) : info.Work.Top + (int)(8 * scale);
        }
        SetWindowPos(Hwnd, -1, x, y, w, h, SwpNoActivate); // HWND_TOPMOST
        // Rounded corners: a layered window at a single alpha has no per-pixel transparency, so clip the window.
        var radius = (int)(24 * scale);
        SetWindowRgn(Hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, radius, radius), true);
        _island.SiteBridge.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, w, h));
        _island.SiteBridge.Show();
        ShowWindow(Hwnd, SwShowNoActivate);
        Bounds = (x, y, x + w, y + h);
        Visible = true;
    }

    public void Hide()
    {
        if (!Visible) return;
        ShowWindow(Hwnd, SwHide);
        Visible = false;
    }

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // A click on a button must never take focus from the app the user is typing in.
        if (msg == WmMouseActivate) return MaNoActivate;
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _island.Dispose();
        DestroyWindow(Hwnd);
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx cls);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
