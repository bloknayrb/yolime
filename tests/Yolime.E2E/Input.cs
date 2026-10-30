using System.Runtime.InteropServices;

namespace Yolime.E2E;

/// <summary>SendInput helpers. Right Ctrl is the default hold key; it's an extended key.</summary>
internal static class Input
{
    public const ushort RCtrl = 0xA3;
    public const ushort LShift = 0xA0;
    public const ushort Esc = 0x1B;
    public const ushort C = 0x43;
    public const ushort A = 0x41;
    public const ushort Enter = 0x0D;
    public const ushort F10 = 0x79;

    public static void Down(ushort vk) => Send(vk, up: false);

    public static void Up(ushort vk) => Send(vk, up: true);

    public static void Tap(ushort vk)
    {
        Down(vk);
        Thread.Sleep(30);
        Up(vk);
    }

    /// <summary>Holds the key, sending auto-repeat key-downs like a real keyboard does.</summary>
    public static void Hold(ushort vk, int ms)
    {
        Down(vk);
        var end = Environment.TickCount64 + ms;
        Thread.Sleep(Math.Min(ms, 500));
        while (Environment.TickCount64 < end)
        {
            Down(vk);
            Thread.Sleep(33);
        }
        Up(vk);
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        var inputs = new[] { Mouse(0x0002), Mouse(0x0004) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void Send(ushort vk, bool up)
    {
        var extended = vk is RCtrl or 0xA5 or 0x5B ? 0x1u : 0u;
        var inputs = new[]
        {
            new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk, Scan = (ushort)MapVirtualKey(vk, 0), Flags = extended | (up ? 0x2u : 0u) } } },
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Types text as Unicode key events (KEYEVENTF_UNICODE), one character at a time.</summary>
    public static void Type(string text)
    {
        foreach (var ch in text)
        {
            var inputs = new[]
            {
                new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Scan = ch, Flags = 0x4 } } },
                new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Scan = ch, Flags = 0x4 | 0x2 } } },
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            Thread.Sleep(10);
        }
    }

    private static INPUT Mouse(uint flags) => new() { Type = 0, U = new InputUnion { Mi = new MOUSEINPUT { Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT Ki; [FieldOffset(0)] public MOUSEINPUT Mi; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int Dx; public int Dy; public uint Data; public uint Flags; public uint Time; public nint Extra; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
