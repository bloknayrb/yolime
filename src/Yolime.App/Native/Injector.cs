using System.Runtime.InteropServices;
using Yolime.Core.Hotkeys;
using Yolime.Core.Output;

namespace Yolime.App.Native;

/// <summary>Sends the paste chord, but only once the user has let go of every modifier.</summary>
internal sealed class Injector : IInjector
{
    private const ushort VkLShift = 0xA0;
    private const ushort VkLControl = 0xA2;
    private const ushort VkInsert = 0x2D;
    private const ushort VkV = 0x56;
    // Both sides of Shift, Ctrl and Alt, and both Windows keys (there's no generic Windows-key VK).
    private static readonly int[] Modifiers = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    public static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(3);

    public async Task WaitForModifiersUpAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ModifierWait;
        while (Modifiers.Any(vk => User32.GetAsyncKeyState(vk) < 0))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("modifiers still held");
            await Task.Delay(10, ct);
        }
    }

    public bool Paste(PasteChord chord)
    {
        // The wait above ran before the target snapshot and clipboard work; a modifier pressed since then would turn
        // Ctrl+V into another shortcut (Ctrl+Alt+V is AltGr+V on some layouts). Refuse, and let the caller copy instead.
        if (Modifiers.Any(vk => User32.GetAsyncKeyState(vk) < 0)) return false;
        // Left Ctrl, never Right: Right Ctrl is the default hold key, and the E2E build accepts injected keys.
        var (modifiers, key, extended) = chord switch
        {
            PasteChord.ShiftInsert => (new[] { VkLShift }, VkInsert, true),
            PasteChord.CtrlShiftV => (new[] { VkLControl, VkLShift }, VkV, false),
            _ => (new[] { VkLControl }, VkV, false),
        };
        var inputs = new List<User32.Input>();
        foreach (var m in modifiers) inputs.Add(Key(m, down: true, extended: false));
        inputs.Add(Key(key, down: true, extended));
        inputs.Add(Key(key, down: false, extended));
        foreach (var m in modifiers.Reverse()) inputs.Add(Key(m, down: false, extended: false));
        var all = inputs.ToArray();
        var sent = User32.SendInput((uint)all.Length, all, Marshal.SizeOf<User32.Input>());
        if (sent > 0 && sent < all.Length)
        {
            // A partial send could leave our modifiers held down in the target: release every key regardless.
            var release = new List<User32.Input> { Key(key, down: false, extended) };
            foreach (var m in modifiers.Reverse()) release.Add(Key(m, down: false, extended: false));
            var r = release.ToArray();
            User32.SendInput((uint)r.Length, r, Marshal.SizeOf<User32.Input>());
        }
        return sent == all.Length;
    }

    private static User32.Input Key(ushort vk, bool down, bool extended) => new()
    {
        Type = User32.InputKeyboard,
        U = new User32.InputUnion
        {
            Ki = new User32.KeybdInput
            {
                Vk = vk,
                // Some apps read scan codes rather than VKs, so send both.
                Scan = (ushort)User32.MapVirtualKeyW(vk, 0),
                Flags = (down ? 0 : User32.KeyEventKeyUp) | (extended ? User32.KeyEventExtendedKey : 0),
                // Marks our own input so the hook never treats it as the user's.
                ExtraInfo = HookReducer.OwnInputMarker,
            },
        },
    };
}
