using System.Diagnostics;
using System.Runtime.InteropServices;
using Yolime.Core.Capture;
using Yolime.Core.Feedback;
using Yolime.Core.Hotkeys;
using Yolime.Core.Output;
using Yolime.Core.Settings;

namespace Yolime.App.Shell;

/// <summary>
/// The notification-area icon, its menu and its balloons, on a hidden top-level window. Not a message-only window:
/// those never receive broadcasts, so the icon would vanish for good after an Explorer restart, and session-end
/// messages would never arrive. The same window receives lock/unlock and suspend/resume notifications, and the
/// paste-last chord (RegisterHotKey, not the keyboard hook: a chord needs no hold logic).
/// </summary>
internal sealed partial class TrayIcon : INoticeSink, ISounds, IDisposable
{
    private const uint WmTray = 0x8000 + 2;
    private const uint WmCommand = 0x0111;
    private const uint WmHotkey = 0x0312;
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmEndSession = 0x0016;
    private const uint WmClipboardUpdate = 0x031D;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint ModNoRepeat = 0x4000;
    private const int PasteLastHotkeyId = 1;
    private const int CmdRetry = 1;
    private const int CmdPause = 2;
    private const int CmdExit = 3;
    private const int CmdPasteLast = 4;
    private const int CmdPauseHistory = 5;
    private const int CmdSettings = 6;
    private const int CmdFinishSetup = 7;
    private const int CmdHistory = 8;
    private const int CmdInstallUpdate = 9;

    private readonly WndProc _wndProc;
    private readonly uint _taskbarCreated;
    private readonly Dictionary<TrayIconState, nint> _icons;
    private readonly Func<bool> _sounds;
    // The whole notice, so a click on its balloon can go where that notice points (its version included).
    private Notice? _lastBalloon;
    private Yolime.Core.Updates.AppVersion? _offered;
    private TrayState _state = new(TrayIconState.Idle, "yolime: ready");
    private string _pasteLastLabel = "Paste last result";
    private bool _historyPaused;

    public TrayIcon(Func<bool> sounds)
    {
        _sounds = sounds;
        _wndProc = OnMessage;
        var cls = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = GetModuleHandleW(null),
            ClassName = "yolime-tray",
        };
        RegisterClassExW(ref cls);
        // WS_POPUP + WS_EX_TOOLWINDOW, never shown: a real top-level window that stays invisible and off Alt+Tab.
        Hwnd = CreateWindowExW(0x80, "yolime-tray", "yolime", 0x80000000, 0, 0, 0, 0, 0, 0, cls.Instance, 0);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        WTSRegisterSessionNotification(Hwnd, 0);
        RegisterSuspendResumeNotification(Hwnd, 0);
        // Feeds clipboard freshness: Reply may use the clipboard only if the user copied it recently.
        ClipboardListening = AddClipboardFormatListener(Hwnd);
        // States differ by shape as well as colour: a ring for "needs you" (paused, setup), a dot otherwise.
        _icons = new()
        {
            [TrayIconState.Idle] = Dot(0xFF8BC34A, ring: false),
            [TrayIconState.Recording] = Dot(0xFFE02020, ring: false),
            [TrayIconState.Paused] = Dot(0xFF9E9E9E, ring: true),
            [TrayIconState.SetupNeeded] = Dot(0xFFF5A623, ring: true),
            [TrayIconState.Elevated] = Dot(0xFF7E57C2, ring: false),
        };
        Add();
    }

    public nint Hwnd { get; }

    /// <summary>False if Windows refused the clipboard listener: Reply then never uses the clipboard as context.</summary>
    public bool ClipboardListening { get; }

    public bool HotkeysPaused { get; private set; }

    /// <summary>
    /// The app window the user was last in, for Paste last and Retry from the menu. Clicking the tray icon makes the
    /// taskbar the foreground window, so the window in front when the menu opens is never the right target.
    /// </summary>
    public Func<nint>? LastAppWindow { get; set; }

    /// <summary>Why the paste-last chord isn't registered, for the tooltip and Settings; null when it is.</summary>
    public string? HotkeyProblem { get; private set; }

    public event Action? RetryRequested;
    public event Action<bool>? PauseChanged;
    public event Action<bool>? HistoryPauseChanged;
    public event Action? PasteLastRequested;
    public event Action? SettingsRequested;
    public event Action? HistoryRequested;
    public event Action? FinishSetupRequested;
    public event Action? ExitRequested;

    /// <summary>"Install update X…" from the menu, or a click on the "available" balloon.</summary>
    public event Action? InstallUpdateRequested;

    /// <summary>A click on "Updated to X": the release page for that version.</summary>
    public event Action<Yolime.Core.Updates.AppVersion>? ReleasePageRequested;

    /// <summary>A click on "The update didn't install": setup's log.</summary>
    public event Action? UpdateLogRequested;

    /// <summary>A key or settings notice's balloon was clicked: open Settings on the page that fixes it.</summary>
    public event Action<Yolime.Core.Overlay.SettingsPage>? SettingsPageRequested;

    /// <summary>The version the menu offers to install (and the tooltip mentions), or null for none.</summary>
    public void SetOfferedUpdate(Yolime.Core.Updates.AppVersion? version)
    {
        _offered = version;
        Update();
    }
    public event Action? SessionEnded;
    public event Action? SessionResumed;
    public event Action? EndingSession;

    /// <summary>The clipboard changed (anyone's change, ours included: freshness tells them apart).</summary>
    public event Action? ClipboardChanged;

    /// <summary>The icon and tooltip, from <see cref="TrayState.From"/> so they never disagree.</summary>
    public void SetState(TrayState state)
    {
        _state = state;
        Update();
    }

    /// <summary>The check mark on "Pause history", kept in step with settings.json.</summary>
    public void SetHistoryPaused(bool paused) => _historyPaused = paused;

    /// <summary>
    /// Registers the paste-last chord, replacing any earlier one. False (with <see cref="HotkeyProblem"/> set) when the
    /// text isn't a chord or another app already holds it. Call on the thread that owns the tray window.
    /// </summary>
    public bool RegisterPasteLast(string chordText)
    {
        UnregisterHotKey(Hwnd, PasteLastHotkeyId);
        var ok = HotkeyChord.TryParse(chordText, out var chord)
            && RegisterHotKey(Hwnd, PasteLastHotkeyId, chord.Modifiers | ModNoRepeat, chord.Vk);
        HotkeyProblem = ok ? null
            : chord == default ? "the paste-last shortcut isn't valid"
            : chordText + " is taken by another app";
        _pasteLastLabel = ok ? "Paste last result\t" + chordText : "Paste last result";
        Update();
        return ok;
    }

    public void Show(Notice notice)
    {
        _lastBalloon = notice;
        var data = Data(0x10); // NIF_INFO
        data.Info = Notices.Text(notice);
        data.InfoTitle = "yolime";
        data.InfoFlags = Notices.IsInfo(notice.Kind) ? 0x1u : 0x2u; // info / warning
        Shell_NotifyIconW(1, ref data); // NIM_MODIFY
    }

    public void PlayModeCue(Yolime.Core.Mode mode)
    {
        if (!_sounds()) return;
        // Off the caller's thread: the cue is several beeps and the hook thread must never wait.
        var count = (int)mode + 1;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
            {
                MessageBeep(0x0u);
                await Task.Delay(140);
            }
        });
    }

    public void Play(SoundKind sound)
    {
        if (!_sounds()) return;
        // System sounds for now (PR4 adds yolime's own). Unlike balloons, Do Not Disturb doesn't mute them.
        MessageBeep(sound switch
        {
            SoundKind.Start => 0x0u,
            SoundKind.Stop => 0x40u,
            SoundKind.Warning => 0x30u,
            _ => 0x10u,
        });
    }

    private void Add()
    {
        var data = Data(0x1 | 0x2 | 0x4); // NIF_MESSAGE | NIF_ICON | NIF_TIP
        Shell_NotifyIconW(0, ref data); // NIM_ADD
        data.TimeoutOrVersion = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIconW(4, ref data); // NIM_SETVERSION
    }

    private void Update()
    {
        var data = Data(0x2 | 0x4);
        Shell_NotifyIconW(1, ref data);
    }

    private string Tooltip()
    {
        return TrayState.Compose(_state.Tooltip, _offered is not null, HotkeyProblem);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Hwnd = Hwnd,
        Id = 1,
        Flags = flags,
        CallbackMessage = WmTray,
        Icon = _icons[_state.Icon],
        Tip = Tooltip(),
    };

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated)
        {
            Add();
            return 0;
        }
        switch (msg)
        {
            case WmTray:
                var evt = (uint)(lParam & 0xFFFF);
                if (evt is WmRButtonUp or WmContextMenu) ShowMenu();
                // Left click opens History; while setup is needed, it goes where setup is finished instead.
                else if (evt is NinSelect or NinKeySelect)
                {
                    if (_state.Icon == TrayIconState.SetupNeeded) FinishSetupRequested?.Invoke();
                    else HistoryRequested?.Invoke();
                }
                else if (evt == NinBalloonUserClick) BalloonClicked();
                return 0;
            case WmHotkey:
                // "Pause hotkeys" pauses the chord too.
                if (wParam == PasteLastHotkeyId && !HotkeysPaused) PasteLastRequested?.Invoke();
                return 0;
            case WmCommand:
                // Posted by tests (the E2E harness opens Settings this way) and by tools/install.ps1 -Force (Exit, so an
                // upgrade gets a clean shutdown); the menu runs its own command directly.
                RunCommand((int)(wParam & 0xFFFF));
                return 0;
            case WmClipboardUpdate:
                ClipboardChanged?.Invoke();
                return 0;
            case WmWtsSessionChange:
                // WTS_CONSOLE_DISCONNECT 2, WTS_REMOTE_DISCONNECT 4, WTS_SESSION_LOCK 7, WTS_SESSION_UNLOCK 8.
                if (wParam is 2 or 4 or 7) SessionEnded?.Invoke();
                else if (wParam == 8) SessionResumed?.Invoke();
                return 0;
            case WmPowerBroadcast:
                // PBT_APMSUSPEND 4, PBT_APMRESUMEAUTOMATIC 0x12.
                if (wParam == 4) SessionEnded?.Invoke();
                else if (wParam == 0x12) SessionResumed?.Invoke();
                return 1;
            case WmEndSession:
                if (wParam != 0) EndingSession?.Invoke();
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>A click on the last balloon goes where that notice points.</summary>
    private void BalloonClicked()
    {
        switch (_lastBalloon)
        {
            case { Kind: NoticeKind.MicDenied }:
                Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
                break;
            case { Kind: NoticeKind.UpdateAvailable or NoticeKind.UpdateAttemptFailed }:
                InstallUpdateRequested?.Invoke();
                break;
            case { Kind: NoticeKind.Updated, Version: { } v }:
                ReleasePageRequested?.Invoke(v);
                break;
            // UpdateSlow is a balloon when a full-screen app hides the notch (OverlayController.Post).
            case { Kind: NoticeKind.UpdateFailed or NoticeKind.UpdateSlow }:
                UpdateLogRequested?.Invoke();
                break;
            case { Kind: var kind } when Notices.SettingsPage(kind) is { } page:
                SettingsPageRequested?.Invoke(page);
                break;
        }
    }

    private void RunCommand(int command)
    {
        switch (command)
        {
            case CmdRetry: RetryRequested?.Invoke(); break;
            case CmdPasteLast: PasteLastRequested?.Invoke(); break;
            case CmdPause:
                HotkeysPaused = !HotkeysPaused;
                PauseChanged?.Invoke(HotkeysPaused);
                break;
            // The check mark follows settings.json (SetHistoryPaused), so a save that fails never shows a lie.
            case CmdPauseHistory: HistoryPauseChanged?.Invoke(!_historyPaused); break;
            case CmdSettings: SettingsRequested?.Invoke(); break;
            case CmdHistory: HistoryRequested?.Invoke(); break;
            case CmdFinishSetup: FinishSetupRequested?.Invoke(); break;
            case CmdExit: ExitRequested?.Invoke(); break;
            case CmdInstallUpdate: InstallUpdateRequested?.Invoke(); break;
        }
    }

    private void ShowMenu()
    {
        const uint Checked = 0x8, Separator = 0x800;
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, CmdPasteLast, _pasteLastLabel);
        AppendMenuW(menu, 0, CmdRetry, "Retry last failed");
        AppendMenuW(menu, Separator, 0, null);
        AppendMenuW(menu, HotkeysPaused ? Checked : 0u, CmdPause, "Pause hotkeys");
        AppendMenuW(menu, _historyPaused ? Checked : 0u, CmdPauseHistory, "Pause history");
        AppendMenuW(menu, Separator, 0, null);
        if (_state.Icon == TrayIconState.SetupNeeded) AppendMenuW(menu, 0, CmdFinishSetup, "Finish setup…");
        AppendMenuW(menu, 0, CmdSettings, "Settings…");
        AppendMenuW(menu, 0, CmdHistory, "History\u2026");
        if (_offered is { } offered)
        {
            AppendMenuW(menu, Separator, 0, null);
            AppendMenuW(menu, 0, CmdInstallUpdate, $"Install update {offered}…");
        }
        AppendMenuW(menu, Separator, 0, null);
        AppendMenuW(menu, 0, CmdExit, "Exit");
        GetCursorPos(out var pt);
        // The menu needs our window in front to close when the user clicks elsewhere.
        SetForegroundWindow(Hwnd);
        var command = TrackPopupMenuCmd(menu, 0x0020 | 0x0100, pt.X, pt.Y, 0, Hwnd, 0); // TPM_RIGHTBUTTON | TPM_RETURNCMD
        PostMessageW(Hwnd, 0, 0, 0);
        DestroyMenu(menu);
        if (command == 0) return;
        // Paste last and retry must land in the user's app, not this hidden window (which the planner refuses) or the
        // taskbar. Other commands keep the foreground, so Settings doesn't open behind the app.
        if (command is CmdPasteLast or CmdRetry && LastAppWindow?.Invoke() is { } target && target != 0 && IsWindow(target))
            SetForegroundWindow(target);
        RunCommand(command);
    }

    /// <summary>A 16x16 filled dot, or a ring, in one colour.</summary>
    private static nint Dot(uint argb, bool ring)
    {
        const int size = 16;
        var pixels = new int[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - 7.5;
            var dy = y - 7.5;
            var d = dx * dx + dy * dy;
            pixels[y * size + x] = d <= 42 && (!ring || d >= 16) ? unchecked((int)argb) : 0;
        }
        var color = CreateBitmap(size, size, 1, 32, pixels);
        var mask = CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        var info = new IconInfo { IsIcon = true, Mask = mask, Color = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        UnregisterHotKey(Hwnd, PasteLastHotkeyId);
        var data = Data(0);
        Shell_NotifyIconW(2, ref data); // NIM_DELETE
        foreach (var icon in _icons.Values) DestroyIcon(icon);
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // These structs need ByValTStr marshalling, which LibraryImport doesn't generate.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx cls);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint RegisterSuspendResumeNotification(nint recipient, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    /// <summary>TrackPopupMenu with TPM_RETURNCMD, which returns the chosen item's id (0 for none).</summary>
    [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenu")]
    private static partial int TrackPopupMenuCmd(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point pt);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int w, int h, uint planes, uint bitCount, [In] int[] bits);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int w, int h, uint planes, uint bitCount, [In] byte[] bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [DllImport("user32.dll")]
#pragma warning disable SYSLIB1054
    private static extern nint CreateIconIndirect(ref IconInfo info);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);
}
