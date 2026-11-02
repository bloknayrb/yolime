using Yolime.Core.Design;

namespace Yolime.Core.Tests.Design;

public class PaletteTests
{
    private const double Text = 4.5;
    private const double Ui = 3.0;

    public static TheoryData<string> Themes => new() { "light", "dark" };

    private static ThemeTokens T(string theme) => theme == "dark" ? Palette.Dark : Palette.Light;

    [Fact]
    public void Ratio_MatchesKnownValues()
    {
        Assert.Equal(21.0, Contrast.Ratio(Rgb.Hex(0xFFFFFF), Rgb.Hex(0x000000)), 2);
        Assert.Equal(1.0, Contrast.Ratio(Rgb.Hex(0x8BC34A), Rgb.Hex(0x8BC34A)), 3);
        // The reason bright lime is decoration only on light surfaces.
        Assert.Equal(1.89, Contrast.Ratio(Rgb.Hex(0x8BC34A), Rgb.Hex(0xF3F3F3)), 2);
        Assert.Equal(Contrast.Ratio(Rgb.Hex(0x123456), Rgb.Hex(0xABCDEF)), Contrast.Ratio(Rgb.Hex(0xABCDEF), Rgb.Hex(0x123456)), 6);
    }

    [Fact]
    public void Over_BlendsPerChannel()
    {
        Assert.Equal(Rgb.Hex(0x808080), Rgb.Hex(0xFFFFFF).Over(Rgb.Hex(0x000000), 0.5));
        Assert.Equal(Rgb.Hex(0x36600C), Rgb.Hex(0x36600C).Over(Rgb.Hex(0xFFFFFF), 1));
    }

    [Theory, MemberData(nameof(Themes))]
    public void BodyText_Reads_OnEverySurface(string theme)
    {
        var t = T(theme);
        foreach (var surface in Surfaces(t))
        {
            AssertAtLeast(Text, t.Text, surface, "text");
            AssertAtLeast(Text, t.TextMuted, surface, "muted text");
            AssertAtLeast(Text, t.AccentText, surface, "link");
        }
    }

    [Theory, MemberData(nameof(Themes))]
    public void AccentFill_LabelReads_AtRestHoverAndPressed(string theme)
    {
        var t = T(theme);
        AssertAtLeast(Text, t.OnAccent, t.AccentFill, "accent label");
        // WinUI's hover and pressed accent fills are the same colour at 90% and 80% over what's behind the button.
        foreach (var under in Surfaces(t))
        {
            AssertAtLeast(Text, t.OnAccent, t.AccentFill.Over(under, 0.9), "hovered accent label");
            AssertAtLeast(Text, t.OnAccent, t.AccentFill.Over(under, 0.8), "pressed accent label");
        }
    }

    [Theory, MemberData(nameof(Themes))]
    public void Indicators_StandOut_FromWhatTheySitOn(string theme)
    {
        var t = T(theme);
        foreach (var surface in Surfaces(t))
        {
            AssertAtLeast(Ui, t.AccentFill, surface, "toggle on / checkbox / nav pill");
            AssertAtLeast(Ui, t.MeterFill, surface, "mic meter");
            AssertAtLeast(Ui, t.ControlBorder, surface, "input border");
        }
        AssertAtLeast(Ui, t.AccentFill, t.NavSelected, "nav pill on the selected item");
        AssertAtLeast(Ui, t.MeterFill, t.MeterTrack, "meter against its track");
    }

    [Theory, MemberData(nameof(Themes))]
    public void HistoryDiff_Reads(string theme)
    {
        var t = T(theme);
        AssertAtLeast(Text, t.DiffAdded, t.ContentBackground, "added");
        AssertAtLeast(Text, t.DiffRemoved, t.ContentBackground, "removed");
        AssertAtLeast(Text, t.DiffAdded, t.ControlBackground, "added in a box");
        AssertAtLeast(Text, t.DiffRemoved, t.ControlBackground, "removed in a box");
    }

    [Fact]
    public void Themes_UseTheirEndOfTheRamp()
    {
        Assert.Equal(Palette.AccentDark1, Palette.Light.AccentFill);
        Assert.Equal(Palette.AccentDark2, Palette.Light.AccentText);
        Assert.Equal(Palette.AccentLight2, Palette.Dark.AccentFill);
        Assert.Equal(Palette.AccentLight3, Palette.Dark.AccentText);
    }

    private static IEnumerable<Rgb> Surfaces(ThemeTokens t) =>
        [t.NotchBackground, t.WindowTint, t.ContentBackground, t.ControlBackground, t.NavSelected];

    private static void AssertAtLeast(double floor, Rgb fg, Rgb bg, string what)
    {
        var ratio = Contrast.Ratio(fg, bg);
        Assert.True(ratio >= floor, $"{what}: {fg} on {bg} is {ratio:0.00}:1, needs {floor}:1");
    }
}
