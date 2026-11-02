namespace Yolime.Core.Overlay;

/// <summary>
/// The overlay card's shape, in DIP, in one place: the XAML border, the window region and the width the content gets
/// all follow from these. The corner stays 12 (not the design's 16) because the window region that shapes it is
/// 1-bit, so a bigger corner shows its steps more.
/// </summary>
public static class OverlayGeometry
{
    /// <summary>The widest a card gets (Listening and Working always take it).</summary>
    public const double MaxWidthDip = 560;

    public const double CornerRadiusDip = 12;
    public const double PaddingH = 20;
    public const double PaddingV = 13;

    /// <summary>Kept at 1 and painted the surface colour outside high contrast, so layout is the same in every theme.</summary>
    public const double Border = 1;

    /// <summary>The room the content has across a full-width card.</summary>
    public static double StackWidth => MaxWidthDip - 2 * (PaddingH + Border);

    /// <summary>CreateRoundRectRgn takes the corner's diameter, in physical pixels.</summary>
    public static int RegionDiameterPx(double scale) => (int)Math.Round(2 * CornerRadiusDip * scale);
}
