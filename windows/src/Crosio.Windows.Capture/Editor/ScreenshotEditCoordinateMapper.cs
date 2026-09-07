using System.Drawing;

namespace Crosio.Windows.Capture.Editor;

public static class ScreenshotEditCoordinateMapper
{
    public static RectangleF ImageBounds(SizeF canvasSize, Size sourcePixelSize)
    {
        if (canvasSize.Width <= 0 || canvasSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasSize));
        }

        if (sourcePixelSize.Width <= 0 || sourcePixelSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePixelSize));
        }

        var scale = Math.Min(
            canvasSize.Width / sourcePixelSize.Width,
            canvasSize.Height / sourcePixelSize.Height);
        var width = sourcePixelSize.Width * scale;
        var height = sourcePixelSize.Height * scale;
        return new RectangleF(
            (canvasSize.Width - width) / 2,
            (canvasSize.Height - height) / 2,
            width,
            height);
    }

    public static bool TryCanvasToSource(
        PointF canvasPoint,
        SizeF canvasSize,
        Size sourcePixelSize,
        out ScreenshotPoint sourcePoint)
    {
        var imageBounds = ImageBounds(canvasSize, sourcePixelSize);
        if (!imageBounds.Contains(canvasPoint))
        {
            sourcePoint = default;
            return false;
        }

        sourcePoint = new ScreenshotPoint(
            Math.Clamp(
                (canvasPoint.X - imageBounds.Left) * sourcePixelSize.Width / imageBounds.Width,
                0,
                sourcePixelSize.Width - 1),
            Math.Clamp(
                (canvasPoint.Y - imageBounds.Top) * sourcePixelSize.Height / imageBounds.Height,
                0,
                sourcePixelSize.Height - 1));
        return true;
    }

    public static PointF SourceToCanvas(
        ScreenshotPoint sourcePoint,
        SizeF canvasSize,
        Size sourcePixelSize)
    {
        var bounds = ImageBounds(canvasSize, sourcePixelSize);
        return new PointF(
            bounds.Left + (sourcePoint.X * bounds.Width / sourcePixelSize.Width),
            bounds.Top + (sourcePoint.Y * bounds.Height / sourcePixelSize.Height));
    }
}
