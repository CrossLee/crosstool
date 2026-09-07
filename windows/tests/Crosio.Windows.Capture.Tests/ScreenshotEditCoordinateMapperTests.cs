using System.Drawing;
using Crosio.Windows.Capture.Editor;

namespace Crosio.Windows.Capture.Tests;

public sealed class ScreenshotEditCoordinateMapperTests
{
    [Fact]
    public void LetterboxedCanvasMapsThroughNamedImageBounds()
    {
        var sourceSize = new Size(1000, 500);
        var canvasSize = new SizeF(1000, 1000);

        var bounds = ScreenshotEditCoordinateMapper.ImageBounds(canvasSize, sourceSize);
        var mapped = ScreenshotEditCoordinateMapper.TryCanvasToSource(
            new PointF(500, 500),
            canvasSize,
            sourceSize,
            out var sourcePoint);

        Assert.Equal(new RectangleF(0, 250, 1000, 500), bounds);
        Assert.True(mapped);
        Assert.Equal(500, sourcePoint.X, precision: 3);
        Assert.Equal(250, sourcePoint.Y, precision: 3);
    }

    [Fact]
    public void PointOutsideDisplayedImageIsRejected()
    {
        var mapped = ScreenshotEditCoordinateMapper.TryCanvasToSource(
            new PointF(500, 100),
            new SizeF(1000, 1000),
            new Size(1000, 500),
            out _);

        Assert.False(mapped);
    }

    [Fact]
    public void SourceToCanvasAndBackRemainAlignedAfterResize()
    {
        var original = new ScreenshotPoint(731.25f, 216.5f);
        var sourceSize = new Size(1400, 900);
        var canvasSize = new SizeF(823, 477);

        var canvasPoint = ScreenshotEditCoordinateMapper.SourceToCanvas(
            original,
            canvasSize,
            sourceSize);
        var mapped = ScreenshotEditCoordinateMapper.TryCanvasToSource(
            canvasPoint,
            canvasSize,
            sourceSize,
            out var roundTrip);

        Assert.True(mapped);
        Assert.Equal(original.X, roundTrip.X, precision: 3);
        Assert.Equal(original.Y, roundTrip.Y, precision: 3);
    }
}
