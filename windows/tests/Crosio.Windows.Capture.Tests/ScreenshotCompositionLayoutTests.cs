using Crosio.Windows.Capture.Composition;
using Crosio.Windows.Capture.Selection;

namespace Crosio.Windows.Capture.Tests;

public sealed class ScreenshotCompositionLayoutTests
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void DeviceFrameKeepsSourcePixelsOneToOneInsideGenericShell()
    {
        var layout = FramedScreenshotLayout.Create(1920, 1080);

        Assert.Equal(1920, layout.ScreenBounds.Width);
        Assert.Equal(1080, layout.ScreenBounds.Height);
        Assert.True(layout.CanvasWidth > layout.ScreenBounds.Width);
        Assert.True(layout.CanvasHeight > layout.ScreenBounds.Height);
        Assert.Equal(layout.LidBounds.Bottom, layout.BaseBounds.Top);
    }

    [Fact]
    public void MultiWindowLayoutPreservesNegativeDesktopOffsets()
    {
        var left = new WindowCompositeItem(
            new CapturedImage(Png, 400, 300),
            new PixelRect(-500, 20, 400, 300),
            "Left");
        var right = new WindowCompositeItem(
            new CapturedImage(Png, 500, 350),
            new PixelRect(100, -100, 500, 350),
            "Right");

        var layout = MultiWindowCompositeLayout.Create([left, right]);

        Assert.Equal(MultiWindowCompositeLayout.Padding, layout.Placements[0].DestinationBounds.Left);
        Assert.Equal(148, layout.Placements[0].DestinationBounds.Top);
        Assert.Equal(628, layout.Placements[1].DestinationBounds.Left);
        Assert.Equal(MultiWindowCompositeLayout.Padding, layout.Placements[1].DestinationBounds.Top);
        Assert.Equal(1_156, layout.CanvasWidth);
        Assert.Equal(476, layout.CanvasHeight);
    }

    [Fact]
    public void MultiWindowLayoutRejectsImplicitEmptySelection()
    {
        Assert.Throws<ArgumentException>(
            () => MultiWindowCompositeLayout.Create([]));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(20, 0)]
    public void CountdownScheduleUsesElapsedTimeRatherThanTimerTickCount(
        double elapsedSeconds,
        int expected)
    {
        Assert.Equal(
            expected,
            ScreenshotCountdownSchedule.RemainingSeconds(
                3,
                TimeSpan.FromSeconds(elapsedSeconds)));
    }
}
