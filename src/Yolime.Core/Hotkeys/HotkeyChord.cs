namespace Yolime.Core.Hotkeys;

/// <summary>
/// A registered chord such as "Win+Alt+V" (paste last result). <see cref="Modifiers"/> uses RegisterHotKey's MOD_*
/// bits. At least one modifier is required, so a chord can never steal a plain typing key.
/// </summary>
public readonly record struct HotkeyChord(uint Modifiers, uint Vk)
{
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
        uint mods = 0;
        foreach (var p in parts[..^1])
        {
            mods |= p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" => ModWin,
                _ => 0x8000, // unknown: refused below
            };
        }
        if (mods == 0 || (mods & 0x8000) != 0) return false;
        if (KeyOf(parts[^1]) is not { } vk) return false;
        // Ctrl+Alt+Del is the secure attention sequence; Windows never delivers it.
        if (vk == 0x2E && (mods & (ModControl | ModAlt)) == (ModControl | ModAlt)) return false;
        chord = new HotkeyChord(mods, vk);
        return true;
    }

    private static uint? KeyOf(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return char.ToUpperInvariant(key[0]);
        if (key.Length is >= 2 and <= 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
            return (uint)(0x6F + f);
        return key.ToLowerInvariant() switch
        {
            "del" or "delete" => 0x2E,
            "insert" or "ins" => 0x2D,
            "space" => 0x20,
            _ => null,
        };
    }
}
