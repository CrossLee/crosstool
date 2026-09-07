using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Crosio.Windows.Capture.Pinning;

internal sealed class PinnedScreenshotSnapshot : IDisposable
{
    private PinnedScreenshotSnapshot(Bitmap bitmap, Size sourcePixelSize)
    {
        Bitmap = bitmap;
        SourcePixelSize = sourcePixelSize;
        ByteCost = checked((long)bitmap.Width * bitmap.Height * 4);
    }

    internal Bitmap Bitmap { get; }

    internal Size SourcePixelSize { get; }

    internal long ByteCost { get; }

    internal static PinnedScreenshotSnapshot Create(
        ReadOnlySpan<byte> pngBytes,
        long maximumPixelCount)
    {
        if (maximumPixelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPixelCount));
        }

        try
        {
            using var stream = new MemoryStream(pngBytes.ToArray(), writable: false);
            using var decoded = Image.FromStream(
                stream,
                useEmbeddedColorManagement: true,
                validateImageData: true);
            if (decoded.Width <= 0 || decoded.Height <= 0)
            {
                throw new PinnedScreenshotException("The pinned screenshot has no pixels.");
            }

            var sourceSize = new Size(decoded.Width, decoded.Height);
            var sourcePixelCount = checked((long)decoded.Width * decoded.Height);
            var targetSize = sourcePixelCount <= maximumPixelCount
                ? sourceSize
                : DownsampledSize(sourceSize, maximumPixelCount);
            var bitmap = new Bitmap(
                targetSize.Width,
                targetSize.Height,
                PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    decoded,
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    0,
                    0,
                    decoded.Width,
                    decoded.Height,
                    GraphicsUnit.Pixel);
            }

            return new PinnedScreenshotSnapshot(bitmap, sourceSize);
        }
        catch (PinnedScreenshotException)
        {
            throw;
        }
        catch (Exception error) when (
            error is ArgumentException
            or OutOfMemoryException)
        {
            throw new PinnedScreenshotException("The screenshot could not be decoded for pinning.", error);
        }
    }

    public void Dispose() => Bitmap.Dispose();

    private static Size DownsampledSize(Size sourceSize, long maximumPixelCount)
    {
        var sourcePixelCount = checked((long)sourceSize.Width * sourceSize.Height);
        var scale = Math.Sqrt((double)maximumPixelCount / sourcePixelCount);
        var width = Math.Max(1, (int)Math.Floor(sourceSize.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(sourceSize.Height * scale));
        while (checked((long)width * height) > maximumPixelCount)
        {
            if (width >= height)
            {
                width--;
            }
            else
            {
                height--;
            }
        }

        return new Size(width, height);
    }
}
