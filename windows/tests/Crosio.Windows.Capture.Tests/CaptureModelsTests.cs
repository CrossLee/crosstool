namespace Crosio.Windows.Capture.Tests;

public sealed class CaptureModelsTests
{
    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];

    [Fact]
    public void PixelRectPreservesNegativeVirtualDesktopCoordinates()
    {
        var rectangle = new PixelRect(-1920, -200, 1920, 1080);

        Assert.Equal(-1920, rectangle.Left);
        Assert.Equal(-200, rectangle.Top);
        Assert.Equal(0, rectangle.Right);
        Assert.Equal(880, rectangle.Bottom);
        Assert.Equal(2_073_600, rectangle.PixelCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void PixelRectRejectsEmptyOrNegativeDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRect(0, 0, width, height));
    }

    [Fact]
    public void CapturedImageOwnsAnIndependentEagerPngCopy()
    {
        var source = (byte[])PngHeader.Clone();
        var image = new CapturedImage(source, 640, 480);

        source[0] = 0;
        var firstRead = image.CopyPngBytes();
        firstRead[1] = 0;

        Assert.Equal(0x89, image.PngBytes.Span[0]);
        Assert.Equal(0x50, image.PngBytes.Span[1]);
        Assert.Equal(640, image.PixelWidth);
        Assert.Equal(480, image.PixelHeight);
    }

    [Fact]
    public void CapturedImageRejectsNonPngPayload()
    {
        Assert.Throws<ArgumentException>(() => new CapturedImage([1, 2, 3, 4], 1, 1));
    }

    [Fact]
    public void RegionRequestCannotBeReinterpretedAsAnotherCaptureKind()
    {
        var bounds = new PixelRect(20, 30, 400, 250);
        var request = new ScreenshotCaptureRequest(new RegionCaptureTarget(bounds));

        Assert.Equal(ScreenshotCaptureKind.Region, request.Target.Kind);
        Assert.Equal(bounds, Assert.IsType<RegionCaptureTarget>(request.Target).Bounds);
        Assert.False(request.IncludeCursor);
    }
}
