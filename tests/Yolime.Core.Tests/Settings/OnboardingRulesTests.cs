using Yolime.Core.Hotkeys;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Settings;

public class OnboardingRulesTests
{
    [Theory]
    [InlineData(0xA3, HoldKeyVerdict.Allowed)] // Right Ctrl
    [InlineData(0xA1, HoldKeyVerdict.Allowed)] // Right Shift
    [InlineData(0x13, HoldKeyVerdict.Allowed)] // Pause
    [InlineData(0xA2, HoldKeyVerdict.LeftModifier)] // the mic would open on every Ctrl+C
    [InlineData(0xA0, HoldKeyVerdict.LeftModifier)]
    [InlineData(0xA4, HoldKeyVerdict.Alt)]
    [InlineData(0xA5, HoldKeyVerdict.Alt)]
    [InlineData(0x5B, HoldKeyVerdict.Win)]
    [InlineData(0x5C, HoldKeyVerdict.Win)]
    [InlineData(0x14, HoldKeyVerdict.Lock)] // Caps Lock would toggle with every dictation
    [InlineData(0x91, HoldKeyVerdict.Lock)]
    [InlineData(0x90, HoldKeyVerdict.Lock)]
    [InlineData(0x41, HoldKeyVerdict.Typing)]
    [InlineData(0x7B, HoldKeyVerdict.Typing)] // F12
    [InlineData(0x7C, HoldKeyVerdict.Typing)] // F13: only remappers send it, and their input is injected
    [InlineData(0x1B, HoldKeyVerdict.Typing)]
    public void HoldKeys_AreJudged_WithAReasonForRefusals(int vk, HoldKeyVerdict expected)
    {
        Assert.Equal(expected, HoldKeyRules.Judge(vk));
        Assert.Equal(expected == HoldKeyVerdict.Allowed, HookReducer.IsValidHoldKey(vk));
        Assert.False(string.IsNullOrWhiteSpace(HoldKeyRules.Reason(expected)));
    }

    [Theory]
    [InlineData(0xA3, "Right Ctrl")]
    [InlineData(0xA1, "Right Shift")]
    [InlineData(0x13, "Pause")]
    public void AllowedKeys_HaveNames(int vk, string name) => Assert.Equal(name, HoldKeyRules.Name(vk));

    [Fact]
    public void NewFields_Default()
    {
        var s = new YolimeSettings();
        Assert.Null(s.MicDeviceId);
        Assert.False(s.HistoryPaused);
        Assert.False(s.FirstRunDone);
        Assert.Equal("Win+Alt+V", s.PasteLastHotkey); // Ctrl+Alt+V is Paste Special in Office (Bryan, 2026-10-28)
    }

    [Theory]
    [InlineData("Ctrl+Alt+V", true, 0x0002 | 0x0001, 0x56)]
    [InlineData("ctrl + shift + F9", true, 0x0002 | 0x0004, 0x78)]
    [InlineData("Win+Alt+P", true, 0x0008 | 0x0001, 0x50)]
    [InlineData("V", false, 0, 0)] // no modifier: it would steal a typing key everywhere
    [InlineData("Ctrl+", false, 0, 0)]
    [InlineData("Ctrl+Alt+Del", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void Chords_Parse(string text, bool ok, uint modifiers, uint vk)
    {
        Assert.Equal(ok, HotkeyChord.TryParse(text, out var chord));
        if (ok) Assert.Equal((modifiers, vk), (chord.Modifiers, chord.Vk));
    }

    [Fact]
    public void TheWizard_ShowsOnlyUntilItsBeenDoneOrSkipped()
    {
        Assert.True(FirstRun.ShowWizard(new YolimeSettings()));
        Assert.False(FirstRun.ShowWizard(new YolimeSettings { FirstRunDone = true }));
    }

    [Theory]
    [InlineData(true, false, true, false, false, TrayIconState.Recording)]
    [InlineData(false, true, true, false, false, TrayIconState.Paused)]
    [InlineData(false, false, false, false, false, TrayIconState.SetupNeeded)]
    [InlineData(false, false, true, true, false, TrayIconState.SetupNeeded)]
    [InlineData(false, false, true, false, true, TrayIconState.Elevated)]
    [InlineData(false, false, true, false, false, TrayIconState.Idle)]
    public void TrayState_PicksTheMostUrgent(bool recording, bool paused, bool sttReady, bool micDenied, bool elevated, TrayIconState expected) =>
        Assert.Equal(expected, TrayState.From(recording, paused, sttReady, micDenied, elevated).Icon);

    [Fact]
    public void SetupNeeded_SaysWhy()
    {
        Assert.Contains("key", TrayState.From(false, false, false, false, false).Tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("microphone", TrayState.From(false, false, true, true, false).Tooltip, StringComparison.OrdinalIgnoreCase);
        // A kept-ready mic is said in the tooltip, since Windows shows the microphone as in use.
        Assert.EndsWith("microphone open", TrayState.From(false, false, true, false, false, micOpen: true).Tooltip);
        Assert.DoesNotContain("open", TrayState.From(true, false, true, false, false, micOpen: true).Tooltip);
    }
}

public class CapturedKeyTests
{
    [Fact]
    public void AnAllowedKey_KeepsItsCode() => Assert.Equal(new CapturedKey(0xA1, HoldKeyVerdict.Allowed), CapturedKey.From(0xA1));

    [Theory]
    [InlineData(0x41, HoldKeyVerdict.Typing)]
    [InlineData(0x14, HoldKeyVerdict.Lock)]
    [InlineData(0xA4, HoldKeyVerdict.Alt)]
    public void ARefusedKey_ReportsOnlyItsCategory(int vk, HoldKeyVerdict verdict) => Assert.Equal(new CapturedKey(null, verdict), CapturedKey.From(vk));
}
