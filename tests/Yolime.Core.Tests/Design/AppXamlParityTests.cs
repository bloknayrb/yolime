using System.Xml.Linq;
using Yolime.Core.Design;

namespace Yolime.Core.Tests.Design;

/// <summary>App.xaml repeats Palette for WinUI's theme resources; these keep the copy honest.</summary>
public class AppXamlParityTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly (string Key, Func<ThemeTokens, Rgb> Value)[] Tokens =
    [
        ("yolime.Text", t => t.Text),
        ("yolime.Text.Muted", t => t.TextMuted),
        ("yolime.Content.Background", t => t.ContentBackground),
        ("yolime.Control.Background", t => t.ControlBackground),
        ("yolime.Control.Border", t => t.ControlBorder),
        ("yolime.Divider", t => t.Divider),
        ("yolime.Nav.Selected", t => t.NavSelected),
        ("yolime.Accent.Fill", t => t.AccentFill),
        ("yolime.Accent.OnFill", t => t.OnAccent),
        ("yolime.Accent.Text", t => t.AccentText),
        ("yolime.Meter.Fill", t => t.MeterFill),
        ("yolime.Meter.Track", t => t.MeterTrack),
        // Outside high contrast the outline is the track itself (no visible line).
        ("yolime.Meter.Outline", t => t.MeterTrack),
        ("yolime.Diff.Added", t => t.DiffAdded),
        ("yolime.Diff.Removed", t => t.DiffRemoved),
        ("AccentFillColorDefaultBrush", t => t.AccentFill),
        ("AccentTextFillColorPrimaryBrush", t => t.AccentText),
        ("TextOnAccentFillColorPrimaryBrush", t => t.OnAccent),
        ("NavigationViewItemBackgroundSelected", t => t.NavSelected),
        ("NavigationViewContentBackground", t => t.ContentBackground),
        ("ListViewItemBackgroundSelected", t => t.NavSelected),
    ];

    private static readonly (string Key, Rgb Value)[] Ramp =
    [
        ("SystemAccentColor", Palette.Accent),
        ("SystemAccentColorLight1", Palette.AccentLight1),
        ("SystemAccentColorLight2", Palette.AccentLight2),
        ("SystemAccentColorLight3", Palette.AccentLight3),
        ("SystemAccentColorDark1", Palette.AccentDark1),
        ("SystemAccentColorDark2", Palette.AccentDark2),
        ("SystemAccentColorDark3", Palette.AccentDark3),
    ];

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ThemeDictionary_MatchesPalette(string theme)
    {
        var dict = Dictionary(theme);
        var tokens = theme == "Dark" ? Palette.Dark : Palette.Light;
        foreach (var (key, value) in Tokens)
            Assert.True(string.Equals(value(tokens).ToString(), ColorOf(dict, key), StringComparison.OrdinalIgnoreCase), $"{theme} {key}: App.xaml has {ColorOf(dict, key)}, Palette has {value(tokens)}");
        foreach (var (key, value) in Ramp)
            Assert.True(string.Equals(value.ToString(), ColorOf(dict, key), StringComparison.OrdinalIgnoreCase), $"{theme} {key}");
    }

    [Fact]
    public void EveryThemeDefinesTheSameYolimeKeys()
    {
        var light = YolimeKeys("Light");
        Assert.NotEmpty(light);
        Assert.Equal(light, YolimeKeys("Dark"));
        Assert.Equal(light, YolimeKeys("HighContrast"));
    }

    [Fact]
    public void HighContrast_OverridesNothingOfWinUis()
    {
        var keys = Dictionary("HighContrast").Elements().Select(e => (string?)e.Attribute(X + "Key")).ToList();
        Assert.All(keys, k => Assert.StartsWith("yolime.", k));
    }

    private static string[] YolimeKeys(string theme) =>
        Dictionary(theme).Elements().Select(e => (string?)e.Attribute(X + "Key") ?? "")
            .Where(k => k.StartsWith("yolime.", StringComparison.Ordinal)).Order().ToArray();

    private static string? ColorOf(XElement dict, string key)
    {
        var e = dict.Elements().FirstOrDefault(el => (string?)el.Attribute(X + "Key") == key);
        if (e is null) return null;
        return e.Name.LocalName == "Color" ? e.Value.Trim() : (string?)e.Attribute("Color");
    }

    private static XElement Dictionary(string theme)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot.Find(), "src", "Yolime.App", "App.xaml"));
        return doc.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(X + "Key") == theme);
    }
}
