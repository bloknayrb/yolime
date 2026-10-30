using Yolime.Core.Output;

namespace Yolime.Core.Tests.Output;

public class OutputPlannerTests
{
    private static readonly TargetSnapshot Word = new(100, 1, "WINWORD", "OpusApp", "42.1", "_WwG", IsPassword: false, IsEditable: true, IsElevated: false);
    private static readonly TargetSnapshot Terminal = new(200, 2, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "7.1", "TermControl", false, null, false);

    private static OutputItem Item(string text, Mode mode = Mode.Dictate, DeliveryIntent intent = DeliveryIntent.Paste) =>
        new(text, intent, false, mode, Guid.NewGuid());

    [Fact]
    public void Paste_ByDefault()
    {
        var d = OutputPlanner.Plan(Item("hello"), Word, Word);
        Assert.Equal(new OutputDecision(OutputAction.Paste, "hello", OutputNotice.None, PasteChord.CtrlV), d);
    }

    [Fact]
    public void YolimesOwnWindows_AreNeverPastedInto()
    {
        var settings = Word with { ProcessName = "yolime", IsOwnProcess = true };
        var d = OutputPlanner.Plan(Item("hello"), settings, settings);
        Assert.Equal(OutputAction.ClipboardOnly, d.Action);
        Assert.Equal(OutputNotice.CopiedOwnWindow, d.Notice);
        // The practice box is in yolime's process too, but the App doesn't mark it as its own.
        var practice = Word with { ProcessName = "yolime" };
        Assert.Equal(OutputAction.Paste, OutputPlanner.Plan(Item("hello"), practice, practice).Action);
    }

    [Fact]
    public void Rule1_PasswordAtStart_Refused()
    {
        var pw = Word with { IsPassword = true };
        Assert.Equal((OutputAction.Refuse, OutputNotice.Refused), Pair(OutputPlanner.Plan(Item("x"), pw, pw)));
        Assert.Equal(OutputAction.Refuse, OutputPlanner.Plan(Item("x"), pw, Word with { Hwnd = 5 }).Action);
    }

    [Fact]
    public void PasswordOnlyNow_NeverPasted_Copied()
    {
        // Started in a normal field, focus moved into a password box: don't type there, but keep the dictation.
        var d = OutputPlanner.Plan(Item("x"), Word, Word with { IsPassword = true });
        Assert.Equal((OutputAction.ClipboardOnly, OutputNotice.CopiedTargetChanged), Pair(d));
    }

    [Fact]
    public void NoStartSnapshot_Copies()
    {
        // A retry from the tray: the target is whatever had focus before the tray click, so never paste.
        Assert.Equal((OutputAction.ClipboardOnly, OutputNotice.CopiedClipboardOnly), Pair(OutputPlanner.Plan(Item("x"), null, Word)));
    }

    [Fact]
    public void Rule2_TargetChanged_Copies()
    {
        Assert.Equal(OutputNotice.CopiedTargetChanged, OutputPlanner.Plan(Item("x"), Word, Word with { Hwnd = 101 }).Notice);
        Assert.Equal(OutputNotice.CopiedTargetChanged, OutputPlanner.Plan(Item("x"), Word, Word with { FocusRuntimeId = "42.2" }).Notice);
        var bothUnknown = Word with { FocusRuntimeId = null };
        Assert.Equal(OutputAction.Paste, OutputPlanner.Plan(Item("x"), bothUnknown, bothUnknown).Action);
    }

    [Fact]
    public void NullRuntimeIdOnOneSide_FallsBackToHwnd()
    {
        // Chromium builds its accessibility tree lazily: the first UIA query often times out.
        Assert.Equal(OutputAction.Paste, OutputPlanner.Plan(Item("x"), Word with { FocusRuntimeId = null }, Word).Action);
        Assert.Equal(OutputAction.Paste, OutputPlanner.Plan(Item("x"), Word, Word with { FocusRuntimeId = null }).Action);
        Assert.Equal(OutputNotice.CopiedTargetChanged, OutputPlanner.Plan(Item("x"), Word with { FocusRuntimeId = null }, Word with { Hwnd = 7 }).Notice);
    }

    [Fact]
    public void Rule3_Elevated_Copies()
    {
        var admin = Word with { IsElevated = true };
        Assert.Equal((OutputAction.ClipboardOnly, OutputNotice.CopiedElevated), Pair(OutputPlanner.Plan(Item("x"), admin, admin)));
    }

    [Fact]
    public void Rule4_TerminalReply_Copies()
    {
        Assert.Equal((OutputAction.ClipboardOnly, OutputNotice.CopiedTerminalReply), Pair(OutputPlanner.Plan(Item("x", Mode.Reply), Terminal, Terminal)));
        Assert.Equal(OutputAction.Paste, OutputPlanner.Plan(Item("x", Mode.Prompt), Terminal, Terminal).Action);
    }

    [Fact]
    public void Rule5_ClipboardOnlyIntent_Copies() =>
        Assert.Equal((OutputAction.ClipboardOnly, OutputNotice.CopiedClipboardOnly), Pair(OutputPlanner.Plan(Item("x", intent: DeliveryIntent.ClipboardOnly), Word, Word)));

    [Fact]
    public void Rule6_NotEditable_Copies_UnknownStillPastes()
    {
        var ro = Word with { IsEditable = false };
        Assert.Equal(OutputNotice.CopiedNotEditable, OutputPlanner.Plan(Item("x"), ro, ro).Notice);
        var unknown = Word with { IsEditable = null };
        Assert.Equal(OutputAction.Paste, OutputPlanner.Plan(Item("x"), unknown, unknown).Action);
    }

    [Theory]
    [InlineData("ConsoleWindowClass", "conhost", null, true)]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal", null, true)]
    [InlineData("mintty", "mintty", null, true)]
    [InlineData("Chrome_WidgetWin_1", "Code", "xterm-helper-textarea", true)]
    [InlineData("Chrome_WidgetWin_1", "Code", "native-edit-context", false)]
    [InlineData("OpusApp", "WINWORD", null, false)]
    [InlineData("PuTTY", "putty", null, true)]
    [InlineData("VirtualConsoleClass", "ConEmu64", null, true)]
    [InlineData("Window Class", "alacritty", null, true)]
    [InlineData("org.wezfurlong.wezterm", "wezterm-gui", null, true)]
    [InlineData("Qt5QWindowIcon", "kitty", null, true)]
    [InlineData("Chrome_WidgetWin_1", "Tabby", null, true)]
    [InlineData("Chrome_WidgetWin_1", "Hyper", null, true)]
    [InlineData("Warp", "warp", null, true)]
    // Any Electron editor's integrated terminal (xterm.js), but never the editor itself.
    [InlineData("Chrome_WidgetWin_1", "Cursor", "xterm-helper-textarea", true)]
    [InlineData("Chrome_WidgetWin_1", "Windsurf", "xterm-helper-textarea", true)]
    [InlineData("Chrome_WidgetWin_1", "VSCodium", "xterm-helper-textarea", true)]
    [InlineData("Chrome_WidgetWin_1", "Cursor", "native-edit-context", false)]
    [InlineData("Chrome_WidgetWin_1", "Windsurf", null, false)]
    public void TerminalDetection(string cls, string proc, string? focusClass, bool expected) =>
        Assert.Equal(expected, TerminalDetector.IsTerminal(Word with { WindowClass = cls, ProcessName = proc, FocusClass = focusClass }));

    [Theory]
    [InlineData("TscShellContainerClass", "mstsc", true)]
    [InlineData("RAIL_WINDOW", "msrdc", true)]
    [InlineData("HwndWrapper[VmConnect", "vmconnect", true)]
    [InlineData("VMPlayerFrame", "vmware", true)]
    [InlineData("VMUIFrame", "vmware-vmx", true)]
    [InlineData("QWidget", "VirtualBoxVM", true)]
    [InlineData("OpusApp", "WINWORD", false)]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal", false)]
    public void RemoteDetection(string cls, string proc, bool expected) =>
        Assert.Equal(expected, TerminalDetector.IsRemote(Word with { WindowClass = cls, ProcessName = proc }));

    [Fact]
    public void RemoteReply_Copies_ButDictatePastes()
    {
        var rdp = Word with { WindowClass = "TscShellContainerClass", ProcessName = "mstsc" };
        // Inside a remote session the real target is invisible to us: a reply to unknown text is never typed into it.
        Assert.Equal((OutputAction.ClipboardOnly, OutputNotice.CopiedRemoteReply), Pair(OutputPlanner.Plan(Item("x", Mode.Reply), rdp, rdp)));
        Assert.Equal(OutputAction.Paste, OutputPlanner.Plan(Item("x"), rdp, rdp).Action);
    }

    [Theory]
    [InlineData("!rm -rf /", "rm -rf /")]
    // Bryan, 2026-10-28: a leading / is kept so dictated slash commands work in terminals (Claude Code).
    [InlineData("/review the diff", "/review the diff")]
    [InlineData("# heading", "heading")]
    [InlineData("!#/ echo hi\n\n", "/ echo hi")]
    public void Terminal_StripsLeadingBangSlashHash(string text, string expected) =>
        Assert.Equal(expected, OutputPlanner.Plan(Item(text, Mode.Prompt), Terminal, Terminal).Text);

    [Fact]
    public void Terminal_CollapsesInternalNewlines_AndStripsControls()
    {
        // A newline pasted into a shell runs the line; ESC can drive the terminal. Neither may get through.
        var d = OutputPlanner.Plan(Item("first line\r\nsecond\u001b[2Jthird\tok", Mode.Prompt), Terminal, Terminal);
        Assert.Equal("first line second[2Jthird\tok", d.Text);
    }

    [Theory]
    [InlineData("Warp", "warp", PasteChord.CtrlShiftV)]
    [InlineData("Window Class", "alacritty", PasteChord.CtrlShiftV)]
    [InlineData("org.wezfurlong.wezterm", "wezterm-gui", PasteChord.CtrlShiftV)]
    [InlineData("Qt5QWindowIcon", "kitty", PasteChord.CtrlShiftV)]
    [InlineData("Chrome_WidgetWin_1", "Hyper", PasteChord.CtrlShiftV)]
    [InlineData("Chrome_WidgetWin_1", "Tabby", PasteChord.CtrlShiftV)]
    [InlineData("PuTTY", "putty", PasteChord.ShiftInsert)]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal", PasteChord.CtrlV)]
    [InlineData("ConsoleWindowClass", "conhost", PasteChord.CtrlV)]
    [InlineData("OpusApp", "WINWORD", PasteChord.CtrlV)]
    public void EachTerminal_GetsItsOwnPasteChord(string cls, string proc, PasteChord expected)
    {
        // Ctrl+V reaches a TUI in these terminals as a literal ^V (talkie 8623739: Claude Code in Warp).
        var t = Word with { WindowClass = cls, ProcessName = proc };
        Assert.Equal(expected, OutputPlanner.Plan(Item("ls", Mode.Prompt), t, t).Chord);
    }

    [Fact]
    public void Mintty_UsesShiftInsert()
    {
        var mintty = Terminal with { WindowClass = "mintty", ProcessName = "mintty" };
        Assert.Equal(PasteChord.ShiftInsert, OutputPlanner.Plan(Item("ls", Mode.Prompt), mintty, mintty).Chord);
        Assert.Equal(PasteChord.CtrlV, OutputPlanner.Plan(Item("ls", Mode.Prompt), Terminal, Terminal).Chord);
    }

    [Fact]
    public void TrailingNewlines_AlwaysStripped() =>
        Assert.Equal("line one\nline two", OutputPlanner.Plan(Item("line one\nline two\r\n\n"), Word, Word).Text);

    [Fact]
    public void NonTerminal_KeepsLeadingSlash() =>
        Assert.Equal("/etc/hosts is the file", OutputPlanner.Plan(Item("/etc/hosts is the file"), Word, Word).Text);

    private static (OutputAction, OutputNotice) Pair(OutputDecision d) => (d.Action, d.Notice);
}
