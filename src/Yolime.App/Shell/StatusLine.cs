using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Yolime.App.Shell;

internal enum StatusKind { Neutral, Ok, Problem }

/// <summary>
/// A muted status line with a check or warning glyph in front, so success and failure aren't told apart by wording
/// alone. Neutral lines ("Not set yet.", "Testing…") have no glyph. The text wraps.
/// </summary>
internal sealed class StatusLine : Grid
{
    private readonly FontIcon _glyph = new() { FontSize = 12, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 6, 0) };
    private readonly TextBlock _text = new() { Style = (Style)Application.Current.Resources["yolime.Muted"] };

    public StatusLine(string text = "", StatusKind kind = StatusKind.Neutral)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumn(_text, 1);
        Children.Add(_glyph);
        Children.Add(_text);
        // The glyph is decoration; the words already say it.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(_glyph, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Set(text, kind);
    }

    public string Text => _text.Text;

    public void Set(string text, StatusKind kind = StatusKind.Neutral)
    {
        _text.Text = text;
        _glyph.Visibility = kind == StatusKind.Neutral ? Visibility.Collapsed : Visibility.Visible;
        _glyph.Glyph = ((char)(kind == StatusKind.Ok ? 0xE73E : 0xE7BA)).ToString(); // CheckMark, Warning
    }
}
