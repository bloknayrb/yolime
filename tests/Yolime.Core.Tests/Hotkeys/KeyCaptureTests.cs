using Yolime.Core.Hotkeys;

namespace Yolime.Core.Tests.Hotkeys;

public class KeyCaptureTests
{
    private readonly KeyCapture _k = new();

    [Fact]
    public void Unarmed_TakesNothing()
    {
        Assert.Equal((false, null), _k.Key(0xA1, up: false, injected: false, ownForeground: true, nowMs: 0));
    }

    [Fact]
    public void Armed_TakesTheNextKey_AndItsKeyUp_ThenDisarms()
    {
        _k.Arm(0);
        Assert.Equal((true, 0xA1), _k.Key(0xA1, up: false, injected: false, ownForeground: true, nowMs: 100));
        Assert.False(_k.Active);
        // The key does nothing on its own: its key-up is swallowed too, and nothing after it.
        Assert.Equal((true, null), _k.Key(0xA1, up: true, injected: false, ownForeground: true, nowMs: 150));
        Assert.Equal((false, null), _k.Key(0x41, up: false, injected: false, ownForeground: true, nowMs: 200));
    }

    [Fact]
    public void AutoRepeat_OfTheCapturedKey_IsSwallowed()
    {
        _k.Arm(0);
        _k.Key(0x13, up: false, injected: false, ownForeground: true, nowMs: 10);
        Assert.Equal((true, null), _k.Key(0x13, up: false, injected: false, ownForeground: true, nowMs: 40));
    }

    [Fact]
    public void InjectedKeys_AreIgnored()
    {
        _k.Arm(0);
        Assert.Equal((false, null), _k.Key(0xA3, up: false, injected: true, ownForeground: true, nowMs: 10));
        Assert.True(_k.Active);
    }

    [Fact]
    public void AKeyUpAlone_IsNotACapture()
    {
        // Settings' own button was clicked with Enter held: its key-up mustn't become the hold key.
        _k.Arm(0);
        Assert.Equal((false, null), _k.Key(0x0D, up: true, injected: false, ownForeground: true, nowMs: 10));
        Assert.True(_k.Active);
    }

    [Fact]
    public void OutsideSettings_ItDisarms_AndTakesNothing()
    {
        _k.Arm(0);
        Assert.Equal((false, null), _k.Key(0xA1, up: false, injected: false, ownForeground: false, nowMs: 10));
        Assert.False(_k.Active);
    }

    [Fact]
    public void ItDisarms_AfterTenSeconds()
    {
        _k.Arm(0);
        Assert.False(_k.Tick(KeyCapture.WindowMs - 1));
        Assert.True(_k.Tick(KeyCapture.WindowMs));
        Assert.False(_k.Active);
        Assert.False(_k.Tick(KeyCapture.WindowMs + 100)); // reported once
        Assert.Equal((false, null), _k.Key(0xA1, up: false, injected: false, ownForeground: true, nowMs: KeyCapture.WindowMs + 200));
    }

    [Fact]
    public void AMissedKeyUp_DoesNotLeaveTheKeySwallowed()
    {
        // Released while an admin window or the secure desktop had the keyboard: the hook never saw the key-up. The
        // key's state can't be asked (Windows doesn't update it for keys a hook blocks), so silence ends the swallow.
        _k.Arm(0);
        _k.Key(0xA1, up: false, injected: false, ownForeground: true, nowMs: 10);
        _k.ReleaseIfQuiet(nowMs: 10 + KeyCapture.QuietMs - 1);
        Assert.True(_k.Busy);
        _k.ReleaseIfQuiet(nowMs: 10 + KeyCapture.QuietMs);
        Assert.False(_k.Busy);
        Assert.Equal((false, null), _k.Key(0xA1, up: false, injected: false, ownForeground: false, nowMs: 5000));
    }

    [Fact]
    public void AHeldKey_StaysSwallowed_WhileItRepeats()
    {
        _k.Arm(0);
        _k.Key(0xA1, up: false, injected: false, ownForeground: true, nowMs: 10);
        for (var t = 500L; t < 4000; t += 30)
        {
            Assert.Equal((true, null), _k.Key(0xA1, up: false, injected: false, ownForeground: true, nowMs: t));
            _k.ReleaseIfQuiet(t + 20);
        }
        Assert.True(_k.Busy);
    }

    [Fact]
    public void YolimesOwnInjectedKey_IsNeverSwallowed()
    {
        // Captured Right Ctrl, then a paste's injected Ctrl+V within the quiet window: the Ctrl must reach the app.
        _k.Arm(0);
        _k.Key(0xA3, up: false, injected: false, ownForeground: true, nowMs: 10);
        Assert.Equal((false, null), _k.Key(0xA3, up: false, injected: true, ownForeground: true, nowMs: 200));
        Assert.Equal((true, null), _k.Key(0xA3, up: true, injected: false, ownForeground: true, nowMs: 250));
    }
}
