using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Yolime.App.Shell;

/// <summary>
/// The mic level: a 6 px rounded track with a fill, coloured from the theme's <c>yolime.Meter.*</c> keys (in high
/// contrast: Highlight on a Window-coloured track outlined in WindowText). Replaces the stock ProgressBar, whose 3 px
/// accent bar read as a progress indicator. Value is 0..1.
/// </summary>
internal sealed class LevelMeter : Grid
{
    public const double Thickness = 6;

    private readonly Border _track = new() { CornerRadius = new CornerRadius(Thickness / 2), BorderThickness = new Thickness(1) };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(Thickness / 2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private double _value;

    public LevelMeter()
    {
        Height = Thickness;
        Children.Add(_track);
        Children.Add(_fill);
        SizeChanged += (_, _) => Layout();
        ThemeColors.Track(this, () =>
        {
            _track.Background = ThemeColors.Brush(this, "yolime.Meter.Track");
            _track.BorderBrush = ThemeColors.Brush(this, "yolime.Meter.Outline");
            _fill.Background = ThemeColors.Brush(this, "yolime.Meter.Fill");
        });
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 1);
            Layout();
        }
    }

    private void Layout()
    {
        var w = ActualWidth * _value;
        // Below the round cap's width a fill would draw as a dot wider than the level; show nothing instead.
        _fill.Width = w < Thickness / 2 ? 0 : w;
    }
}
