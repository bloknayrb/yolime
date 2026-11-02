using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Yolime.App.Native;

namespace Yolime.App.Shell;

internal static class WindowSizing
{
    /// <summary>
    /// Sizes a window for the monitor it opens on (its own DPI, not the system's, so mixed-DPI setups get it right),
    /// never larger than that monitor's work area, and centres it there.
    /// </summary>
    public static void SizeAndCentre(Window window, int widthDip, int heightDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = User32.GetDpiForWindow(hwnd);
        var scale = (dpi == 0 ? User32.GetDpiForSystem() : dpi) / 96.0;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)(widthDip * scale), area.Width);
        var height = Math.Min((int)(heightDip * scale), area.Height);
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        // Every yolime window comes through here: give each the app icon (title bar, taskbar button, Alt+Tab). An
        // unpackaged window doesn't take the exe's icon on its own.
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "yolime.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
    }
}
