namespace Yolime.Core.Output;

/// <summary>Where text is going, read when the run starts and again just before delivery.</summary>
/// <param name="FocusRuntimeId">The UIA focused element's RuntimeId, joined with '.'; null if UIA didn't answer.</param>
/// <param name="IsEditable">Positive evidence only: null means unknown (Chromium often misreports), which still pastes.</param>
public sealed record TargetSnapshot(
    nint Hwnd,
    int ProcessId,
    string ProcessName,
    string WindowClass,
    string? FocusRuntimeId,
    string? FocusClass,
    bool IsPassword,
    bool? IsEditable,
    bool IsElevated,
    bool IsOwnProcess = false);

public enum OutputAction { Paste, ClipboardOnly, Refuse }

public enum OutputNotice { None, CopiedTargetChanged, CopiedTerminalReply, CopiedRemoteReply, CopiedClipboardOnly, CopiedNotEditable, CopiedElevated, CopiedOwnWindow, Refused }

/// <summary>
/// How the target pastes. mintty and PuTTY take Shift+Insert. Several modern terminals pass Ctrl+V through to the
/// program inside as a literal ^V (Claude Code in Warp), and paste on Ctrl+Shift+V.
/// </summary>
public enum PasteChord { CtrlV, ShiftInsert, CtrlShiftV }

public sealed record OutputDecision(OutputAction Action, string Text, OutputNotice Notice, PasteChord Chord = PasteChord.CtrlV);

public static class TerminalDetector
{
    private static readonly HashSet<string> TerminalClasses = new(StringComparer.Ordinal)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "PuTTY", "VirtualConsoleClass",
    };

    // Terminals whose window class is generic (Electron, Qt, winit), so only the process name identifies them.
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "alacritty", "wezterm-gui", "kitty", "Tabby", "Hyper", "Warp", "ConEmu64", "ConEmu",
    };

    // Remote desktops and VM consoles: the real target is inside, out of sight.
    private static readonly HashSet<string> RemoteProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc", "msrdc", "vmconnect", "vmware", "vmware-vmx", "VirtualBoxVM",
    };

    /// <summary>
    /// Console hosts and terminal apps, plus the integrated terminal (xterm.js) of any editor, but not the editor
    /// itself. The single authority for "is this a terminal": profiles and output safety both ask here.
    /// </summary>
    public static bool IsTerminal(TargetSnapshot t) =>
        TerminalClasses.Contains(t.WindowClass)
        || TerminalProcesses.Contains(t.ProcessName)
        || t.FocusClass?.Contains("xterm-helper-textarea", StringComparison.Ordinal) == true;

    private static readonly HashSet<string> CtrlShiftVProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "warp", "alacritty", "wezterm-gui", "kitty", "Hyper", "Tabby",
    };

    public static PasteChord PasteChordFor(TargetSnapshot t)
    {
        if (t.WindowClass is "mintty" or "PuTTY") return PasteChord.ShiftInsert;
        if (CtrlShiftVProcesses.Contains(t.ProcessName)) return PasteChord.CtrlShiftV;
        return PasteChord.CtrlV;
    }

    /// <summary>A remote desktop or VM window: what has focus inside it is unknown.</summary>
    public static bool IsRemote(TargetSnapshot t) =>
        RemoteProcesses.Contains(t.ProcessName) || string.Equals(t.WindowClass, "TscShellContainerClass", StringComparison.Ordinal);
}

/// <summary>Decides how a result is delivered. The first matching rule wins, so the safety rules come first.</summary>
public static class OutputPlanner
{
    /// <param name="atStart">The target when the hold began; null for a retry, which never pastes.</param>
    public static OutputDecision Plan(OutputItem item, TargetSnapshot? atStart, TargetSnapshot now)
    {
        var terminal = TerminalDetector.IsTerminal(now);
        var text = Sanitise(item.Text, terminal);

        // Dictating into a password field is refused outright (the controller also refuses at key-down).
        if (atStart?.IsPassword == true) return new(OutputAction.Refuse, text, OutputNotice.Refused);
        if (atStart is null) return new(OutputAction.ClipboardOnly, text, OutputNotice.CopiedClipboardOnly);
        if (TargetChanged(atStart, now) || now.IsPassword)
            return new(OutputAction.ClipboardOnly, text, OutputNotice.CopiedTargetChanged);
        // yolime's own windows (Settings, the overlay) are never pasted into; the App marks the wizard's practice box
        // as not "own" so dictation can be tried there.
        if (now.IsOwnProcess) return new(OutputAction.ClipboardOnly, text, OutputNotice.CopiedOwnWindow);
        // UIPI blocks SendInput into an elevated window, so the paste would silently do nothing.
        if (now.IsElevated) return new(OutputAction.ClipboardOnly, text, OutputNotice.CopiedElevated);
        if (terminal && item.Mode == Mode.Reply) return new(OutputAction.ClipboardOnly, text, OutputNotice.CopiedTerminalReply);
        if (item.Mode == Mode.Reply && TerminalDetector.IsRemote(now)) return new(OutputAction.ClipboardOnly, text, OutputNotice.CopiedRemoteReply);
        if (item.Intent == DeliveryIntent.ClipboardOnly) return new(OutputAction.ClipboardOnly, text, OutputNotice.CopiedClipboardOnly);
        if (now.IsEditable == false) return new(OutputAction.ClipboardOnly, text, OutputNotice.CopiedNotEditable);
        return new(OutputAction.Paste, text, OutputNotice.None, TerminalDetector.PasteChordFor(now));
    }

    /// <summary>A different window, or a different element in it. A missing RuntimeId (UIA timed out) compares windows only.</summary>
    private static bool TargetChanged(TargetSnapshot atStart, TargetSnapshot now) =>
        atStart.Hwnd != now.Hwnd
        || (atStart.FocusRuntimeId is { } before && now.FocusRuntimeId is { } after && before != after);

    /// <summary>
    /// Output never ends with a newline (a pasted newline can submit a form or run a command). In a terminal, any
    /// line break becomes a space, other control characters (ESC sequences) are dropped, and a leading ! or # is
    /// removed (shell escape and comment). A leading / is kept so slash commands can be dictated.
    /// </summary>
    private static string Sanitise(string text, bool terminal)
    {
        text = text.TrimEnd('\r', '\n');
        if (!terminal) return text;
        var clean = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            if (c is '\r' or '\n') clean.Append(' ');
            else if (c == '\t' || !char.IsControl(c)) clean.Append(c);
        }
        // A leading / stays (Bryan, 2026-10-28): dictated slash commands must work in Claude Code's terminal.
        return clean.ToString().TrimStart('!', '#', ' ', '\t');
    }
}
