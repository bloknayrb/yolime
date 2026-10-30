namespace Yolime.Core.Hotkeys;

/// <summary>
/// Turns a raw hook event into the little the state machine needs. This is where key codes stop: everything after
/// it sees only <see cref="HotkeyKind"/>.
/// </summary>
public sealed class HookReducer(int holdVk)
{
    /// <summary>Put in <c>dwExtraInfo</c> of every SendInput yolime makes, so its own Ctrl+V never reaches the machine.</summary>
    public static readonly nint OwnInputMarker = 0x596F6C69; // "Yoli"

    private const int VkShift = 0x10;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkEscape = 0x1B;

    public HotkeyKind? Key(int vk, bool up, bool injected, nint extraInfo)
    {
        if (extraInfo == OwnInputMarker) return null;
        // Synthetic input (other tools, remappers, or a page script) never drives dictation.
        if (injected && !AcceptInjected) return null;
        if (vk == holdVk) return up ? HotkeyKind.HoldUp : HotkeyKind.HoldDown;
        if (vk is VkShift or VkLShift or VkRShift) return up ? HotkeyKind.ShiftUp : HotkeyKind.ShiftDown;
        // Only presses count as "other input": a key-up can trail a key pressed just before the hold.
        if (up) return null;
        return vk == VkEscape ? HotkeyKind.Esc : HotkeyKind.OtherKey;
    }

    /// <summary>Button presses and wheel turns abort a hold; moving the mouse doesn't.</summary>
    public HotkeyKind? Mouse(int message, bool injected)
    {
        if (injected && !AcceptInjected) return null;
        return message switch
        {
            0x0201 or 0x0204 or 0x0207 or 0x020B or 0x020A or 0x020E => HotkeyKind.Mouse,
            _ => null,
        };
    }

    /// <summary>
    /// A usable hold key: a sided modifier (the generic Shift/Ctrl/Alt VKs never arrive in a low-level hook) or an
    /// ordinary key. Never Esc, the Windows keys or the mouse buttons.
    /// </summary>
    public static bool IsValidHoldKey(int vk) => HoldKeyRules.Judge(vk) == HoldKeyVerdict.Allowed;

#if YOLIME_E2E
    /// <summary>E2E builds only: the Sandbox harness drives the app with SendInput.</summary>
    public static bool AcceptInjected { get; set; }
#else
    private const bool AcceptInjected = false;
#endif

    /// <summary>Whether injected input counts as the user's: false in every build but E2E.</summary>
    public static bool InjectedCounts => AcceptInjected;
}
