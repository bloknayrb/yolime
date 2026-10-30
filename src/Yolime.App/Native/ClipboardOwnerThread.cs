using System.Runtime.InteropServices;
using Yolime.Core.Logging;

namespace Yolime.App.Native;

/// <summary>
/// The clipboard owner: a message-only window on a thread of its own that only answers clipboard messages. Another
/// app's paste of a delayed-rendered offer waits on this thread (WM_RENDERFORMAT is sent to the owner), so it must
/// never wait on yolime's UI thread, a lock, or anything slower than copying bytes into the clipboard.
/// </summary>
/// <remarks>
/// Opens, sets and restores stay on pool threads and pass <see cref="Hwnd"/> to OpenClipboard, which is allowed from
/// any thread. Only the render messages run here.
/// </remarks>
internal sealed partial class ClipboardOwnerThread : IDisposable
{
    public interface IHandler
    {
        void OnRenderFormat(nint hwnd, uint format);
        void OnRenderAllFormats(nint hwnd);
        void OnDestroyClipboard();
    }

    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmRenderFormat = 0x0305;
    private const uint WmRenderAllFormats = 0x0306;
    private const uint WmDestroyClipboard = 0x0307;
    private static readonly nint HwndMessage = -3;

    private readonly ILog _log;
    private readonly Thread _thread;
    // Rooted for the window's lifetime: a collected delegate would crash the next message.
    private readonly WndProc _wndProc;
    private volatile IHandler? _handler;

    public nint Hwnd { get; private set; }

    public ClipboardOwnerThread(ILog log)
    {
        _log = log;
        _wndProc = Proc;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "yolime-clipboard-owner" };
        _thread.Start();
        ready.Wait();
        if (Hwnd == 0) throw new InvalidOperationException("couldn't create the clipboard owner window");
    }

    /// <summary>Set once the guard exists; until then no offer can have been made, so no render can be asked for.</summary>
    public void Attach(IHandler handler) => _handler = handler;

    private void Run(ManualResetEventSlim ready)
    {
        try
        {
            var cls = new WndClassEx
            {
                Size = Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = GetModuleHandleW(null),
                ClassName = "yolime-clipboard",
            };
            RegisterClassExW(ref cls);
            Hwnd = CreateWindowExW(0, cls.ClassName, "yolime-clipboard", 0, 0, 0, 0, 0, HwndMessage, 0, cls.Instance, 0);
        }
        finally
        {
            ready.Set();
        }
        if (Hwnd == 0) return;
        while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(in msg);
            User32.DispatchMessageW(in msg);
        }
    }

    private nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // Nothing may escape: an exception here would take the process down with an unrendered offer on the clipboard.
        try
        {
            switch (msg)
            {
                case WmRenderFormat:
                    _handler?.OnRenderFormat(hwnd, (uint)wParam);
                    return 0;
                case WmRenderAllFormats:
                    _handler?.OnRenderAllFormats(hwnd);
                    return 0;
                case WmDestroyClipboard:
                    _handler?.OnDestroyClipboard();
                    return 0;
                case WmDestroy:
                    PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            _log.Write(LogEvent.StageFailed, LogField.Tag("stage", Core.Stage.Output), LogField.Error(ex));
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Closes the window, whose destruction (while it still owns the clipboard) sends WM_RENDERALLFORMATS, so a pending
    /// paste is written for real before yolime goes. Call after the restore has been flushed.
    /// </summary>
    public void Dispose()
    {
        if (Hwnd != 0 && _thread.IsAlive)
        {
            User32.PostMessageW(Hwnd, WmClose, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx cls);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
