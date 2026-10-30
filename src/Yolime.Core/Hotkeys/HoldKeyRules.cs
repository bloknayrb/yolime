namespace Yolime.Core.Hotkeys;

/// <summary>Why a key can or can't be the hold key. Refusals carry a reason the settings page can show.</summary>
public enum HoldKeyVerdict { Allowed, LeftModifier, Alt, Win, Lock, Typing }

/// <summary>
/// Which keys may be held to dictate. Only keys that do nothing on their own and are rarely part of shortcuts:
/// Right Ctrl, Right Shift and Pause.
/// </summary>
/// <remarks>
/// The hold key isn't swallowed, and the mic opens at key-down. So a left modifier would open the mic on every Ctrl+C
/// or capital letter, a lock key would toggle with every dictation, Alt and Win open menus and Start, and any typing
/// key would type. F13 to F24 exist only through remappers, whose input is injected and therefore ignored.
/// </remarks>
public static class HoldKeyRules
{
    public const int RightCtrl = 0xA3, RightShift = 0xA1, Pause = 0x13;

    public static HoldKeyVerdict Judge(int vk) => vk switch
    {
        RightCtrl or RightShift or Pause => HoldKeyVerdict.Allowed,
        0xA0 or 0xA2 or 0x10 or 0x11 => HoldKeyVerdict.LeftModifier,
        0xA4 or 0xA5 or 0x12 => HoldKeyVerdict.Alt,
        0x5B or 0x5C => HoldKeyVerdict.Win,
        0x14 or 0x90 or 0x91 => HoldKeyVerdict.Lock,
        _ => HoldKeyVerdict.Typing,
    };

    public static string Name(int vk) => vk switch
    {
        RightCtrl => "Right Ctrl",
        RightShift => "Right Shift",
        Pause => "Pause",
        _ => "another key",
    };

    public static string Reason(HoldKeyVerdict verdict) => verdict switch
    {
        HoldKeyVerdict.Allowed => "Hold it, speak, and let go.",
        HoldKeyVerdict.LeftModifier => "Left Ctrl and Left Shift are part of everyday shortcuts: the mic would open every time.",
        HoldKeyVerdict.Alt => "Alt opens menus and access keys in most apps.",
        HoldKeyVerdict.Win => "The Windows key opens Start.",
        HoldKeyVerdict.Lock => "Lock keys would switch on or off with every dictation.",
        _ => "That key types or does something on its own. Use Right Ctrl, Right Shift or Pause.",
    };
}

/// <summary>
/// What a key capture reports to the page: the key code only when it's allowed, otherwise just the category, so a
/// refused key's code never leaves the hook thread.
/// </summary>
public readonly record struct CapturedKey(int? Vk, HoldKeyVerdict Verdict)
{
    public static CapturedKey From(int vk) =>
        HoldKeyRules.Judge(vk) is var verdict && verdict == HoldKeyVerdict.Allowed ? new(vk, verdict) : new(null, verdict);
}
