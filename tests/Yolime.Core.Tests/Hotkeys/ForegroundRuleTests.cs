using Yolime.Core.Hotkeys;

namespace Yolime.Core.Tests.Hotkeys;

public class ForegroundRuleTests
{
    private const int Target = 10;
    private static readonly ForegroundSample Back = new(Target, Visible: true, Cloaked: false, ShellTray: false);
    private static readonly ForegroundSample Other = new(20, Visible: true, Cloaked: false, ShellTray: false);
    private static readonly ForegroundSample Third = new(30, Visible: true, Cloaked: false, ShellTray: false);
    // The Sandbox probe's flash: rgnupdt.exe's console window, foreground for about 67 ms but never shown.
    private static readonly ForegroundSample Hidden = new(40, Visible: false, Cloaked: false, ShellTray: false);
    private static readonly ForegroundSample Cloaked = new(50, Visible: true, Cloaked: true, ShellTray: false);
    private static readonly ForegroundSample Taskbar = new(60, Visible: true, Cloaked: false, ShellTray: true);

    private readonly ForegroundRule _hold = new(ForegroundRule.HoldSettleMs);

    public ForegroundRuleTests() => _hold.Reset(Target);

    [Fact]
    public void AHiddenFlash_ThatGivesTheForegroundBack_NeverFires()
    {
        Assert.Equal(ForegroundVerdict.None, _hold.Observe(Hidden, 1000).Verdict);
        Assert.True(_hold.Pending);
        var back = _hold.Observe(Back, 1067);
        Assert.Equal(ForegroundVerdict.Settled, back.Verdict);
        Assert.Equal(67, back.PendingMs);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(Back, 1300).Verdict);
    }

    [Fact]
    public void AHiddenOrCloakedForeground_AtTheTick_IsUndecided()
    {
        _hold.Observe(Hidden, 1000);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(Hidden, 1500).Verdict);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(Cloaked, 1600).Verdict);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(Taskbar, 1700).Verdict);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(default, 1800).Verdict); // no foreground (UAC, a desktop switch)
        Assert.True(_hold.Pending);
    }

    [Fact]
    public void AWindowCloakedWhenActivated_ThatAppears_StillCounts()
    {
        // A UWP app launching, or a virtual desktop swipe: cloaked at the event, real a moment later.
        _hold.Observe(Cloaked, 1000);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(Cloaked, 1100).Verdict);
        Assert.Equal(ForegroundVerdict.Switched, _hold.Tick(Cloaked with { Cloaked = false }, 1200).Verdict);
    }

    [Fact]
    public void AVisibleSwitch_FiresOnce_AfterTheSettle()
    {
        _hold.Observe(Other, 1000);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(Other, 1199).Verdict);
        Assert.Equal(ForegroundVerdict.Switched, _hold.Tick(Other, 1200).Verdict);
        Assert.False(_hold.Pending);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(Other, 1300).Verdict);
    }

    [Fact]
    public void AVisibleSwitch_ThatComesBackInTime_NeverFires()
    {
        _hold.Observe(Other, 1000);
        Assert.Equal(ForegroundVerdict.Settled, _hold.Tick(Back, 1150).Verdict);
        Assert.Equal(ForegroundVerdict.None, _hold.Tick(Other, 1300).Verdict);
    }

    [Fact]
    public void ASecondSwitch_KeepsTheFirstOnesTime()
    {
        _hold.Observe(Other, 1000);
        _hold.Observe(Third, 1150);
        _hold.Observe(Hidden, 1180);
        // A different real window than the one that started it: still away from the target.
        Assert.Equal(ForegroundVerdict.Switched, _hold.Tick(Third, 1200).Verdict);
    }

    [Fact]
    public void Settling_LastsOnlyTheSettleWindow()
    {
        // A hidden window that keeps the foreground leaves the switch pending for good; holding back the recording
        // card that long would leave the hold in Pressed, uncapped, with Shift and Esc acting as shortcuts.
        _hold.Observe(Hidden, 1000);
        Assert.True(_hold.Settling(1000));
        Assert.True(_hold.Settling(1000 + ForegroundRule.HoldSettleMs + 100));
        Assert.False(_hold.Settling(1000 + ForegroundRule.HoldSettleMs + 101));
        Assert.True(_hold.Pending);
    }

    [Fact]
    public void NotPending_IsNotSettling() => Assert.False(_hold.Settling(1000));

    [Fact]
    public void TheTargetsOwnWindows_AreNeverASwitch()
    {
        Assert.Equal(ForegroundVerdict.None, _hold.Observe(Back, 1000).Verdict);
        Assert.False(_hold.Pending);
    }

    [Fact]
    public void Reset_ClearsAPendingSwitch_AndTakesTheNewTarget()
    {
        _hold.Observe(Other, 1000);
        _hold.Reset(20);
        Assert.False(_hold.Pending);
        Assert.Equal(ForegroundVerdict.None, _hold.Observe(Other, 2000).Verdict);
        Assert.False(_hold.Pending);
    }

    [Fact]
    public void WithNoSettle_ARealSwitchFiresAtTheEvent_AndAFlashWaitsForTheTick()
    {
        var preview = new ForegroundRule(settleMs: 0);
        preview.Reset(Target);
        Assert.Equal(ForegroundVerdict.Switched, preview.Observe(Other, 1000).Verdict);
        preview.Reset(Target);
        Assert.Equal(ForegroundVerdict.None, preview.Observe(Hidden, 2000).Verdict);
        Assert.Equal(ForegroundVerdict.Settled, preview.Tick(Back, 2100).Verdict);
        preview.Observe(Cloaked, 3000);
        Assert.Equal(ForegroundVerdict.Switched, preview.Tick(Cloaked with { Cloaked = false }, 3100).Verdict);
    }

    [Theory]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("Shell_SecondaryTrayWnd", true)]
    [InlineData("NotifyIconOverflowWindow", true)]
    [InlineData("TopLevelWindowForOverflowXamlIsland", true)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData(null, false)]
    public void ShellTrayClasses(string? cls, bool tray) => Assert.Equal(tray, ForegroundSample.IsShellTrayClass(cls));
}
