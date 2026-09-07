namespace Crosio.Windows.LongCapture.Tests;

internal static class FrameFactory
{
    internal static LongCaptureFrame FromContentRows(
        int startRow,
        int height,
        int width = 12)
    {
        var pixels = new byte[checked(width * height * LongCaptureFrame.BytesPerPixel)];
        for (var y = 0; y < height; y++)
        {
            var contentY = startRow + y;
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * LongCaptureFrame.BytesPerPixel;
                pixels[offset] = unchecked((byte)((contentY * 17) + (x * 3)));
                pixels[offset + 1] = unchecked((byte)((contentY * 29) + (x * 7)));
                pixels[offset + 2] = unchecked((byte)((contentY * 43) + (x * 11)));
                pixels[offset + 3] = 255;
            }
        }

        return new LongCaptureFrame(width, height, pixels);
    }

    internal static LongCaptureFrame Solid(int width, int height, byte value)
    {
        var pixels = new byte[checked(width * height * LongCaptureFrame.BytesPerPixel)];
        for (var offset = 0; offset < pixels.Length; offset += LongCaptureFrame.BytesPerPixel)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }

        return new LongCaptureFrame(width, height, pixels);
    }

    internal static LongCaptureFrame PeriodicRows(
        int startRow,
        int height,
        int period = 4,
        int width = 12)
    {
        var pixels = new byte[checked(width * height * LongCaptureFrame.BytesPerPixel)];
        for (var y = 0; y < height; y++)
        {
            var value = unchecked((byte)(((startRow + y) % period) * 50));
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * LongCaptureFrame.BytesPerPixel;
                pixels[offset] = value;
                pixels[offset + 1] = unchecked((byte)(value + x));
                pixels[offset + 2] = unchecked((byte)(value + (x * 2)));
                pixels[offset + 3] = 255;
            }
        }

        return new LongCaptureFrame(width, height, pixels);
    }

    internal static void AssertContentRows(LongCaptureFrame frame, int firstContentRow)
    {
        var expected = FromContentRows(firstContentRow, frame.Height, frame.Width);
        Assert.Equal(expected.CopyPixels(), frame.CopyPixels());
    }
}
