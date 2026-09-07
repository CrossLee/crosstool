using System.Drawing;
using Crosio.Windows.Capture.Selection;

namespace Crosio.Windows.Capture.Tests;

public sealed class CaptureSelectionGeometryTests
{
    [Fact]
    public void RegionNormalizesReverseDragAcrossNegativeCoordinates()
    {
        var region = CaptureSelectionGeometry.RegionFromEndpoints(
            new Point(-100, 300),
            new Point(-700, -50));

        Assert.NotNull(region);
        Assert.Equal(new PixelRect(-700, -50, 600, 350), region.Value);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 100)]
    [InlineData(100, 1)]
    public void RegionRejectsClicksAndSubMinimumDrags(int width, int height)
    {
        var region = CaptureSelectionGeometry.RegionFromEndpoints(
            Point.Empty,
            new Point(width, height));

        Assert.Null(region);
    }
}
