using Yolime.Core.Hotkeys;

namespace Yolime.Core.Tests.Hotkeys;

public class HookReducerTests
{
    private const int RCtrl = 0xA3;
    private static readonly HookReducer R = new(RCtrl);

    private static HotkeyKind? Key(int vk, bool up = false, bool injected = false, nint extra = 0) => R.Key(vk, up, injected, extra);

    [Fact]
    public void HoldKey_DownAndUp() => Assert.Equal((HotkeyKind.HoldDown, HotkeyKind.HoldUp), (Key(RCtrl)!.Value, Key(RCtrl, up: true)!.Value));

    [Fact]
    public void LeftCtrl_IsJustAnotherKey() => Assert.Equal(HotkeyKind.OtherKey, Key(0xA2));

    [Theory]
    [InlineData(0x10)]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    public void Shift_DownAndUp(int vk)
    {
        Assert.Equal(HotkeyKind.ShiftDown, Key(vk));
        Assert.Equal(HotkeyKind.ShiftUp, Key(vk, up: true));
    }

    [Fact]
    public void Esc_DownOnly()
    {
        Assert.Equal(HotkeyKind.Esc, Key(0x1B));
        Assert.Null(Key(0x1B, up: true));
    }

    [Fact]
    public void OtherKeyUps_Ignored()
    {
        // Typing fast then holding Right Ctrl: the last letter's key-up arrives after Ctrl goes down.
        Assert.Equal(HotkeyKind.OtherKey, Key(0x41));
        Assert.Null(Key(0x41, up: true));
    }

    [Fact]
    public void OurOwnInput_AlwaysIgnored()
    {
        Assert.Null(Key(0x56, injected: true, extra: HookReducer.OwnInputMarker));
        Assert.Null(Key(0xA2, injected: true, extra: HookReducer.OwnInputMarker));
    }

    [Fact]
    public void InjectedInput_IgnoredOutsideE2E()
    {
#if YOLIME_E2E
        Assert.Null(Key(RCtrl, injected: true));
        HookReducer.AcceptInjected = true;
        try { Assert.Equal(HotkeyKind.HoldDown, Key(RCtrl, injected: true)); }
        finally { HookReducer.AcceptInjected = false; }
#else
        Assert.Null(Key(RCtrl, injected: true));
        Assert.Null(Key(0x41, injected: true));
#endif
    }

    [Theory]
    [InlineData(0x0200, null)]          // WM_MOUSEMOVE
    [InlineData(0x0201, HotkeyKind.Mouse)] // WM_LBUTTONDOWN
    [InlineData(0x0202, null)]          // WM_LBUTTONUP
    [InlineData(0x0204, HotkeyKind.Mouse)] // WM_RBUTTONDOWN
    [InlineData(0x0207, HotkeyKind.Mouse)] // WM_MBUTTONDOWN
    [InlineData(0x020A, HotkeyKind.Mouse)] // WM_MOUSEWHEEL
    [InlineData(0x020E, HotkeyKind.Mouse)] // WM_MOUSEHWHEEL
    [InlineData(0x020B, HotkeyKind.Mouse)] // WM_XBUTTONDOWN
    public void Mouse_OnlyButtonsDownAndWheel(int message, HotkeyKind? expected) => Assert.Equal(expected, R.Mouse(message, injected: false));

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x1B)]
    [InlineData(0x5B)]
    public void HoldKey_Validation_RejectsGenericModifiersEscAndWin(int vk) => Assert.False(HookReducer.IsValidHoldKey(vk));

    [Theory]
    [InlineData(0xA3)]
    [InlineData(0xA1)]
    [InlineData(0x13)]
    public void HoldKey_Validation_AcceptsTheSafeKeys(int vk) => Assert.True(HookReducer.IsValidHoldKey(vk));
}
