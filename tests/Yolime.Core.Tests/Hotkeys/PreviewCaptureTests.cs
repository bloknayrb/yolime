using Yolime.Core.Hotkeys;

namespace Yolime.Core.Tests.Hotkeys;

public class PreviewCaptureTests
{
    private const int Enter = 0x0D, Esc = 0x1B, C = 0x43, A = 0x41, Up = 0x26, Down = 0x28, PgUp = 0x21, PgDn = 0x22, LShift = 0xA0, RCtrl = 0xA3;
    private readonly PreviewCapture _p = new(holdVk: RCtrl);

    private (bool Swallow, PreviewSignal? Signal) Key(int vk, bool up = false, bool injected = false, bool modifiers = false, long at = 100) =>
        _p.Key(vk, up, injected, 0, modifiers, at);

    [Fact]
    public void Inactive_SwallowsNothing()
    {
        Assert.Equal((false, null), Key(Enter));
        Assert.Equal((false, null), Key(C));
    }

    [Theory]
    [InlineData(Enter, PreviewSignal.Insert)]
    [InlineData(Esc, PreviewSignal.Discard)]
    [InlineData(C, PreviewSignal.Copy)]
    public void TheCardsKeys_AreSwallowed_AndEndIt(int vk, PreviewSignal expected)
    {
        _p.Arm(0, targetPid: 7);
        Assert.Equal((true, expected), Key(vk));
        Assert.False(_p.Active);
    }

    [Fact]
    public void AnEndingKey_IsSwallowedThroughItsKeyUpAndAutoRepeat()
    {
        // Otherwise a held Enter repeats into Slack after Insert and sends the message.
        _p.Arm(0, 7);
        Assert.Equal((true, PreviewSignal.Insert), Key(Enter));
        Assert.Equal((true, null), Key(Enter)); // auto-repeat
        Assert.Equal((true, null), Key(Enter, up: true));
        Assert.Equal((false, null), Key(Enter)); // a later, separate press is the user's again
    }

    [Theory]
    [InlineData(Up, PreviewSignal.ScrollUp)]
    [InlineData(Down, PreviewSignal.ScrollDown)]
    [InlineData(PgUp, PreviewSignal.PageUp)]
    [InlineData(PgDn, PreviewSignal.PageDown)]
    public void ScrollKeys_AreSwallowed_AndKeepTheCard(int vk, PreviewSignal expected)
    {
        _p.Arm(0, 7);
        Assert.Equal((true, expected), Key(vk));
        Assert.True(_p.Active);
        Assert.Equal((true, null), Key(vk, up: true));
    }

    [Fact]
    public void AnyOtherKey_EndsTheCard_AndReachesTheApp()
    {
        _p.Arm(0, 7);
        Assert.Equal((false, PreviewSignal.Expired), Key(A));
        Assert.False(_p.Active);
        Assert.Equal((false, null), Key(C)); // capture is over: the user's typing is theirs
    }

    [Fact]
    public void AShortcut_EndsTheCard_AndIsNotSwallowed()
    {
        _p.Arm(0, 7);
        Assert.Equal((false, PreviewSignal.Expired), Key(C, modifiers: true)); // Ctrl+C is a shortcut, not "copy the card"
    }

    [Fact]
    public void ShiftAndTheHoldKey_PassThrough_WithoutEndingTheCard()
    {
        _p.Arm(0, 7);
        Assert.Equal((false, null), Key(LShift)); // still down from the release that asked for the preview
        Assert.Equal((false, null), Key(RCtrl)); // the hold machine sees it: a tap there cancels the run
        Assert.True(_p.Active);
    }

    [Fact]
    public void InjectedKeys_AreNeverSwallowed_AndNeverAct()
    {
        // KeePass auto-type and other tools send C and Enter: they must neither be eaten nor press the card's buttons.
        _p.Arm(0, 7);
        Assert.Equal((false, null), Key(Enter, injected: true));
        Assert.Equal((false, null), Key(C, injected: true));
        Assert.True(_p.Active);
    }

    [Fact]
    public void KeyUpsForKeysPressedBeforeArming_PassThrough()
    {
        _p.Arm(0, 7);
        Assert.Equal((false, null), Key(A, up: true));
        Assert.True(_p.Active);
    }

    [Fact]
    public void AClickOffTheCard_EndsIt_AClickOnItDoesNot()
    {
        _p.Arm(0, 7);
        Assert.Null(_p.MouseDown(onCard: true));
        Assert.Equal(PreviewSignal.Expired, _p.MouseDown(onCard: false));
        Assert.False(_p.Active);
    }

    private static ForegroundSample Window(int pid, bool visible = true, bool cloaked = false) => new(pid, visible, cloaked, ShellTray: false);

    [Fact]
    public void SwitchingToAnotherApp_EndsIt()
    {
        _p.Arm(0, 7);
        Assert.Null(_p.Foreground(Window(7), 10));
        Assert.Equal(PreviewSignal.Expired, _p.Foreground(Window(8), 20));
    }

    [Fact]
    public void AHiddenFlash_DoesNotEndIt()
    {
        _p.Arm(0, 7);
        Assert.Null(_p.Foreground(Window(9, visible: false), 10));
        Assert.Null(_p.Tick(100, Window(7)));
        Assert.True(_p.Active);
    }

    [Fact]
    public void AnAppCloakedWhenActivated_EndsItWhenItAppears()
    {
        _p.Arm(0, 7);
        Assert.Null(_p.Foreground(Window(8, cloaked: true), 10));
        Assert.Null(_p.Tick(100, Window(8, cloaked: true)));
        Assert.Equal(PreviewSignal.Expired, _p.Tick(200, Window(8)));
    }

    [Fact]
    public void TheDeadline_LivesOnTheHookThread_SoAHungUiCantKeepKeysCaptured()
    {
        _p.Arm(1_000, 7);
        Assert.Null(_p.Tick(1_000 + PreviewCapture.DeadlineMs - 1, Window(7)));
        Assert.Equal(PreviewSignal.Expired, _p.Tick(1_000 + PreviewCapture.DeadlineMs, Window(7)));
        Assert.Equal((false, null), Key(Enter));
    }

    /// <summary>Hovering the card pauses its deadline; leaving moves the deadline on by the time hovered.</summary>
    [Fact]
    public void Hovering_PausesTheDeadline_ByTheTimeHovered()
    {
        _p.Arm(0, 7);
        _p.Hover(true, 5_000);
        Assert.Equal((15_000L, true), _p.Clock(5_000));
        Assert.Null(_p.Tick(30_000, Window(7))); // past 20 s, but hovered
        Assert.Equal((15_000L, true), _p.Clock(30_000));
        _p.Hover(false, 30_000); // 25 s hovered
        Assert.Equal((15_000L, false), _p.Clock(30_000));
        Assert.Null(_p.Tick(44_999, Window(7)));
        Assert.Equal(PreviewSignal.Expired, _p.Tick(45_000, Window(7)));
    }

    /// <summary>The card fails open by the cap even with the pointer parked on it, or a pointer-leave that never came.</summary>
    [Fact]
    public void Hovering_IsCapped_SoAParkedPointerCantHoldKeysForever()
    {
        _p.Arm(0, 7);
        _p.Hover(true, 1_000);
        Assert.Null(_p.Tick(PreviewCapture.DeadlineMs + PreviewCapture.MaxHoverMs - 1, Window(7)));
        Assert.False(_p.Clock(PreviewCapture.DeadlineMs + PreviewCapture.MaxHoverMs - 1).Paused); // the cap is spent
        Assert.Equal(PreviewSignal.Expired, _p.Tick(PreviewCapture.DeadlineMs + PreviewCapture.MaxHoverMs, Window(7)));
        Assert.Equal((false, null), Key(Enter));
    }

    [Fact]
    public void Hovering_TwiceAddsUp_WithinOneCap_AndArmStartsFresh()
    {
        _p.Arm(0, 7);
        _p.Hover(true, 1_000);
        _p.Hover(false, 31_000); // +30 s
        _p.Hover(true, 32_000);
        _p.Hover(false, 52_000); // +10 s: the cap's last 10 s, not 20
        Assert.Equal(PreviewSignal.Expired, _p.Tick(PreviewCapture.DeadlineMs + PreviewCapture.MaxHoverMs, Window(7)));
        _p.Arm(100_000, 7);
        Assert.Equal((PreviewCapture.DeadlineMs, false), _p.Clock(100_000));
        _p.Hover(false, 100_500); // a leave with no enter adds nothing
        Assert.Equal(PreviewSignal.Expired, _p.Tick(100_000 + PreviewCapture.DeadlineMs, Window(7)));
    }

    [Fact]
    public void Disarm_StopsCapture_ButStillSwallowsAPendingKeyUp()
    {
        _p.Arm(0, 7);
        Key(Esc);
        _p.Disarm();
        Assert.Equal((true, null), Key(Esc, up: true));
    }
}
