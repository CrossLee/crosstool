namespace Crosio.Windows.LongCapture.Tests;

public sealed class LongCaptureFrameTests
{
    [Fact]
    public void ConstructorRequiresTightlyPackedRgbaData()
    {
        Assert.Throws<ArgumentException>(() => new LongCaptureFrame(2, 2, new byte[15]));
    }

    [Fact]
    public void FrameOwnsInputAndReturnedCopies()
    {
        var pixels = new byte[16];
        pixels[0] = 20;
        var frame = new LongCaptureFrame(2, 2, pixels);

        pixels[0] = 99;
        var copy = frame.CopyPixels();
        copy[0] = 55;

        Assert.Equal(20, frame.Pixels.Span[0]);
    }
}
