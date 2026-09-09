using Crosio.Windows.Core.Presentation;

namespace Crosio.Windows.Core.Tests.Presentation;

public sealed class WindowPlacementPolicyTests
{
    [Fact]
    public void InvalidWindowSizeUsesKnownMainWindowFallback()
    {
        var window = new DesktopRectangle(4000, 2000, 0, 1200);

        var result = WindowPlacementPolicy.UseFallbackSize(window, 1120, 720);

        Assert.Equal(new DesktopRectangle(4000, 2000, 1120, 720), result);
    }

    [Fact]
    public void ConvertsDisplayRelativeWorkAreaToDesktopCoordinates()
    {
        var displayBounds = new DesktopRectangle(-1920, 100, 1920, 1080);
        var relativeWorkArea = new DesktopRectangle(0, 40, 1920, 1040);

        var result = WindowPlacementPolicy.ToDesktopCoordinates(
            displayBounds,
            relativeWorkArea);

        Assert.Equal(new DesktopRectangle(-1920, 140, 1920, 1040), result);
    }

    [Fact]
    public void WindowOnSecondaryDisplayWithNegativeOriginIsVisible()
    {
        var window = new DesktopRectangle(-1500, 120, 900, 700);
        var workAreas = new[]
        {
            new DesktopRectangle(0, 0, 1920, 1040),
            new DesktopRectangle(-1920, 0, 1920, 1040),
        };

        Assert.True(WindowPlacementPolicy.IntersectsAnyWorkArea(window, workAreas));
    }

    [Fact]
    public void WindowOutsideEveryDisplayIsNotVisible()
    {
        var window = new DesktopRectangle(4000, 2000, 900, 700);
        var workAreas = new[]
        {
            new DesktopRectangle(0, 0, 1920, 1040),
            new DesktopRectangle(1920, 0, 2560, 1400),
        };

        Assert.False(WindowPlacementPolicy.IntersectsAnyWorkArea(window, workAreas));
    }

    [Fact]
    public void TouchingWorkAreaEdgeWithoutOverlapIsNotVisible()
    {
        var window = new DesktopRectangle(1920, 200, 900, 700);
        var workAreas = new[] { new DesktopRectangle(0, 0, 1920, 1040) };

        Assert.False(WindowPlacementPolicy.IntersectsAnyWorkArea(window, workAreas));
    }

    [Theory]
    [InlineData(0, 700)]
    [InlineData(900, 0)]
    [InlineData(-1, 700)]
    public void InvalidWindowSizeIsNotVisible(int width, int height)
    {
        var window = new DesktopRectangle(100, 100, width, height);
        var workAreas = new[] { new DesktopRectangle(0, 0, 1920, 1040) };

        Assert.False(WindowPlacementPolicy.IntersectsAnyWorkArea(window, workAreas));
    }

    [Fact]
    public void CentersWindowInsideWorkArea()
    {
        var window = new DesktopRectangle(4000, 2000, 800, 600);
        var workArea = new DesktopRectangle(20, 40, 1920, 1040);

        var result = WindowPlacementPolicy.CenterInWorkArea(window, workArea);

        Assert.Equal(new DesktopRectangle(580, 260, 800, 600), result);
    }

    [Fact]
    public void OversizedWindowStartsAtWorkAreaOrigin()
    {
        var window = new DesktopRectangle(4000, 2000, 2200, 1200);
        var workArea = new DesktopRectangle(20, 40, 1920, 1040);

        var result = WindowPlacementPolicy.CenterInWorkArea(window, workArea);

        Assert.Equal(new DesktopRectangle(20, 40, 2200, 1200), result);
    }

}
