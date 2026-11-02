namespace Yolime.Core.Design;

/// <summary>An opaque sRGB colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>"#RRGGBB", as App.xaml writes it.</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>This colour drawn at <paramref name="alpha"/> over <paramref name="under"/> (how WinUI's hover and pressed fills land).</summary>
    public Rgb Over(Rgb under, double alpha) => new(Mix(R, under.R, alpha), Mix(G, under.G, alpha), Mix(B, under.B, alpha));

    private static byte Mix(byte top, byte bottom, double a) => (byte)Math.Round(top * a + bottom * (1 - a));
}

/// <summary>One theme's colours. Names match the <c>yolime.*</c> keys in App.xaml.</summary>
public sealed record ThemeTokens(
    Rgb NotchBackground,
    Rgb Text,
    Rgb TextMuted,
    Rgb WindowTint,
    Rgb ContentBackground,
    Rgb ControlBackground,
    Rgb ControlBorder,
    Rgb Divider,
    Rgb NavSelected,
    Rgb AccentFill,
    Rgb OnAccent,
    Rgb AccentText,
    Rgb MeterFill,
    Rgb MeterTrack,
    Rgb DiffAdded,
    Rgb DiffRemoved);

/// <summary>
/// yolime's colours (design "B · Calm &amp; soft", 2026-11-02). The single source: App.xaml repeats these values for
/// WinUI's theme resources, and a test keeps the two equal. High contrast never uses any of them.
/// </summary>
public static class Palette
{
    /// <summary>Opacity of the tint layer laid over Mica in every window.</summary>
    public const double WindowTintOpacity = 0.6;

    // The lime accent ramp, mapped onto WinUI's SystemAccentColor* keys. Light theme fills with Dark1 and links with
    // Dark2; dark theme fills with Light2 and links with Light3. Bright lime itself fails contrast on light surfaces,
    // so it is decoration only there (the icon).
    public static readonly Rgb Accent = Rgb.Hex(0x8BC34A);
    public static readonly Rgb AccentLight1 = Rgb.Hex(0xA6D267);
    public static readonly Rgb AccentLight2 = Rgb.Hex(0xC2E38A);
    public static readonly Rgb AccentLight3 = Rgb.Hex(0xDCF0B8);
    public static readonly Rgb AccentDark1 = Rgb.Hex(0x36600C);
    public static readonly Rgb AccentDark2 = Rgb.Hex(0x2A4C09);
    public static readonly Rgb AccentDark3 = Rgb.Hex(0x1F3906);

    public static readonly ThemeTokens Light = new(
        NotchBackground: Rgb.Hex(0xF1F6E8),
        Text: Rgb.Hex(0x1A1A1A),
        TextMuted: Rgb.Hex(0x5C5C5C),
        WindowTint: Rgb.Hex(0xF2F5EC),
        ContentBackground: Rgb.Hex(0xF8FAF3),
        ControlBackground: Rgb.Hex(0xFFFFFF),
        ControlBorder: Rgb.Hex(0x748463),
        Divider: Rgb.Hex(0xDDE6CF),
        NavSelected: Rgb.Hex(0xE5EDD8),
        AccentFill: AccentDark1,
        OnAccent: Rgb.Hex(0xFFFFFF),
        AccentText: AccentDark2,
        MeterFill: AccentDark1,
        MeterTrack: Rgb.Hex(0xDCE4CF),
        DiffAdded: Rgb.Hex(0x0F7B0F),
        DiffRemoved: Rgb.Hex(0xC42B1C));

    public static readonly ThemeTokens Dark = new(
        NotchBackground: Rgb.Hex(0x22281C),
        Text: Rgb.Hex(0xFFFFFF),
        TextMuted: Rgb.Hex(0xABABAB),
        WindowTint: Rgb.Hex(0x1F231B),
        ContentBackground: Rgb.Hex(0x262C20),
        ControlBackground: Rgb.Hex(0x2C3326),
        ControlBorder: Rgb.Hex(0x7E8D6E),
        Divider: Rgb.Hex(0x39432D),
        NavSelected: Rgb.Hex(0x2F3828),
        AccentFill: AccentLight2,
        OnAccent: Rgb.Hex(0x000000),
        AccentText: AccentLight3,
        MeterFill: AccentLight1,
        MeterTrack: Rgb.Hex(0x3A4330),
        DiffAdded: Rgb.Hex(0x6CCB5F),
        DiffRemoved: Rgb.Hex(0xFF99A4));

    public static ThemeTokens For(bool dark) => dark ? Dark : Light;
}

/// <summary>WCAG 2 contrast.</summary>
public static class Contrast
{
    public static double Ratio(Rgb a, Rgb b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Luminance(Rgb c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte v)
    {
        var s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
