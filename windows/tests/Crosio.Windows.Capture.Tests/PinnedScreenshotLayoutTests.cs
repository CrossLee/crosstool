using System.Drawing;
using Crosio.Windows.Capture.Pinning;

namespace Crosio.Windows.Capture.Tests;

public sealed class PinnedScreenshotLayoutTests
{
    [Fact]
    public void InitialBoundsStaySmallAndVisibleOnNegativeCoordinateDisplay()
    {
        var workingArea = new RectangleF(-1920, -120, 1920, 1040);
        var bounds = PinnedScreenshotLayout.InitialBounds(
            new SizeF(2400, 1200),
            dpiScale: 1,
            workingArea,
            new PointF(-1700, 100));

        AssertContained(bounds, workingArea);
        Assert.Equal(2, bounds.Width / bounds.Height, precision: 3);
        Assert.True(bounds.Width <= 520.01f);
        Assert.True(bounds.Height <= 416.01f);
    }

    [Fact]
    public void InitialBoundsUsePhysicalDpiLimitsWithoutChangingAspectRatio()
    {
        var workingArea = new RectangleF(-3840, 0, 3840, 2080);
        var bounds = PinnedScreenshotLayout.InitialBounds(
            new SizeF(4800, 2400),
            dpiScale: 2,
            workingArea,
            new PointF(-3000, 800));

        AssertContained(bounds, workingArea);
        Assert.Equal(2, bounds.Width / bounds.Height, precision: 3);
        Assert.True(bounds.Width <= 1040.01f);
        Assert.True(bounds.Height <= 832.01f);
    }

    [Fact]
    public void ZoomKeepsTheSameImagePointUnderCursorUntilAnEdgeBinds()
    {
        var workingArea = new RectangleF(0, 0, 1920, 1080);
        var before = new RectangleF(400, 300, 600, 300);
        var pointer = new PointF(550, 375);
        var unitBefore = NormalizedPosition(pointer, before);

        var after = PinnedScreenshotLayout.ZoomedBounds(
            before,
            pointer,
            1.25f,
            new SizeF(120, 60),
            PinnedScreenshotLayout.MaximumSize(workingArea),
            workingArea);

        var unitAfter = NormalizedPosition(pointer, after);
        Assert.Equal(unitBefore.X, unitAfter.X, precision: 4);
        Assert.Equal(unitBefore.Y, unitAfter.Y, precision: 4);
        Assert.Equal(2, after.Width / after.Height, precision: 4);
        AssertContained(after, workingArea);
    }

    [Fact]
    public void ZoomClampsIdempotentlyAtMinimumAndMaximum()
    {
        var workingArea = new RectangleF(-1600, 50, 1600, 900);
        var initial = new RectangleF(-1200, 300, 600, 300);
        var minimum = new SizeF(120, 60);
        var maximum = PinnedScreenshotLayout.MaximumSize(workingArea);

        var minimumBounds = PinnedScreenshotLayout.ZoomedBounds(
            initial,
            new PointF(-900, 450),
            0.01f,
            minimum,
            maximum,
            workingArea);
        var minimumAgain = PinnedScreenshotLayout.ZoomedBounds(
            minimumBounds,
            new PointF(-900, 450),
            0.01f,
            minimum,
            maximum,
            workingArea);
        var maximumBounds = PinnedScreenshotLayout.ZoomedBounds(
            initial,
            new PointF(-900, 450),
            100,
            minimum,
            maximum,
            workingArea);
        var maximumAgain = PinnedScreenshotLayout.ZoomedBounds(
            maximumBounds,
            new PointF(-900, 450),
            100,
            minimum,
            maximum,
            workingArea);

        Assert.Equal(120, minimumBounds.Width, precision: 3);
        AssertRectEqual(minimumBounds, minimumAgain);
        Assert.True(maximumBounds.Width <= maximum.Width + 0.01f);
        Assert.True(maximumBounds.Height <= maximum.Height + 0.01f);
        AssertRectEqual(maximumBounds, maximumAgain);
        AssertContained(maximumBounds, workingArea);
    }

    [Fact]
    public void WheelZoomMapsVerticalWheelAndTrackpadDeltas()
    {
        var wheelIn = PinnedScreenshotLayout.ScaleFactorForWheel(0, 120, false, false);
        var wheelOut = PinnedScreenshotLayout.ScaleFactorForWheel(0, -120, false, false);
        var preciseIn = PinnedScreenshotLayout.ScaleFactorForWheel(0, 4, true, false);

        Assert.NotNull(wheelIn);
        Assert.NotNull(wheelOut);
        Assert.NotNull(preciseIn);
        Assert.Equal(1.1, wheelIn.Value, precision: 3);
        Assert.Equal(1 / 1.1, wheelOut.Value, precision: 3);
        Assert.True(preciseIn.Value > 1);
    }

    [Theory]
    [InlineData(5, 4, false, false)]
    [InlineData(0, 0, false, false)]
    [InlineData(0, 120, false, true)]
    [InlineData(float.NaN, 120, false, false)]
    public void WheelZoomRejectsHorizontalMomentumZeroAndInvalidInput(
        float deltaX,
        float deltaY,
        bool hasPreciseDeltas,
        bool hasMomentum)
    {
        Assert.Null(PinnedScreenshotLayout.ScaleFactorForWheel(
            deltaX,
            deltaY,
            hasPreciseDeltas,
            hasMomentum));
    }

    private static PointF NormalizedPosition(PointF point, RectangleF bounds) => new(
        (point.X - bounds.Left) / bounds.Width,
        (point.Y - bounds.Top) / bounds.Height);

    private static void AssertContained(RectangleF bounds, RectangleF container)
    {
        Assert.True(bounds.Left >= container.Left - 0.01f);
        Assert.True(bounds.Top >= container.Top - 0.01f);
        Assert.True(bounds.Right <= container.Right + 0.01f);
        Assert.True(bounds.Bottom <= container.Bottom + 0.01f);
    }

    private static void AssertRectEqual(RectangleF expected, RectangleF actual)
    {
        Assert.Equal(expected.Left, actual.Left, precision: 3);
        Assert.Equal(expected.Top, actual.Top, precision: 3);
        Assert.Equal(expected.Width, actual.Width, precision: 3);
        Assert.Equal(expected.Height, actual.Height, precision: 3);
    }
}
