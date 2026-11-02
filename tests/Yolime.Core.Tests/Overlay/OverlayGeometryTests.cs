using Yolime.Core.Overlay;

namespace Yolime.Core.Tests.Overlay;

public class OverlayGeometryTests
{
    [Fact]
    public void Corner_StaysAt12()
    {
        // The design's other surfaces use 16; the overlay doesn't, because its region is 1-bit. Changing this is a
        // decision, not a tidy-up: see docs/superpowers/plans/2026-11-02-design-implementation.md.
        Assert.Equal(12, OverlayGeometry.CornerRadiusDip);
    }

    [Theory]
    [InlineData(1.00, 24)]
    [InlineData(1.25, 30)]
    [InlineData(1.50, 36)]
    [InlineData(1.75, 42)]
    [InlineData(2.00, 48)]
    [InlineData(2.50, 60)]
    [InlineData(3.00, 72)]
    public void RegionDiameter_IsTwiceTheRadius_InPhysicalPixels(double scale, int expected) =>
        Assert.Equal(expected, OverlayGeometry.RegionDiameterPx(scale));

    [Fact]
    public void StackWidth_LeavesRoomForPaddingAndBorder()
    {
        Assert.Equal(518, OverlayGeometry.StackWidth);
        Assert.Equal(OverlayGeometry.MaxWidthDip, OverlayGeometry.StackWidth + 2 * (OverlayGeometry.PaddingH + OverlayGeometry.Border));
    }
}
