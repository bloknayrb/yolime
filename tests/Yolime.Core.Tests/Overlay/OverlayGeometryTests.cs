using Yolime.Core.Overlay;

namespace Yolime.Core.Tests.Overlay;

public class OverlayGeometryTests
{
    [Fact]
    public void Corner_StaysAt12()
    {
        // The design's other surfaces use 16; the overlay is 12 by Bryan's decision. Changing this is a decision, not a
        // tidy-up: see docs/superpowers/plans/2026-11-02-design-implementation.md.
        Assert.Equal(12, OverlayGeometry.CornerRadiusDip);
    }

    [Fact]
    public void StackWidth_LeavesRoomForPaddingAndBorder()
    {
        Assert.Equal(518, OverlayGeometry.StackWidth);
        Assert.Equal(OverlayGeometry.MaxWidthDip, OverlayGeometry.StackWidth + 2 * (OverlayGeometry.PaddingH + OverlayGeometry.Border));
    }
}
