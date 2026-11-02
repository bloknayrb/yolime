namespace Yolime.Core.Overlay;

/// <summary>
/// The overlay card's shape, in DIP, in one place: the XAML border and the width the content gets follow from these.
/// The corner is 12, not the design's 16 elsewhere: Bryan's call (2026-11-02) when the corner was still a stepped
/// 1-bit region, kept now that it's antialiased.
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
}
