using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Crosio.Windows.Capture.Composition;

public interface IScreenshotComposer
{
    CapturedImage AddCrosioDeviceFrame(CapturedImage source);

    CapturedImage ComposeSelectedWindows(
        IReadOnlyList<WindowCompositeItem> selectedWindows);
}

/// <summary>
/// Produces fully materialized PNG results for advanced screenshots. These
/// results are handed to ScreenshotCapturePipeline only after composition, so
/// the clipboard never receives a source display or intermediate window.
/// </summary>
public sealed class WindowsScreenshotComposer : IScreenshotComposer
{
    private static readonly Color Accent = Color.FromArgb(255, 86, 92, 255);

    public CapturedImage AddCrosioDeviceFrame(CapturedImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var layout = FramedScreenshotLayout.Create(source.PixelWidth, source.PixelHeight);
        using var sourceStream = new MemoryStream(source.CopyPngBytes(), writable: false);
        using var sourceImage = Image.FromStream(sourceStream, useEmbeddedColorManagement: true);
        using var canvas = NewCanvas(layout.CanvasWidth, layout.CanvasHeight);
        using var graphics = Graphics.FromImage(canvas);
        Configure(graphics);
        graphics.Clear(Color.Transparent);

        DrawSoftShadow(graphics, layout.LidBounds, Math.Max(16, layout.OuterPadding));
        using (var lidPath = RoundedRectangle(layout.LidBounds, Math.Max(18, layout.OuterPadding / 2)))
        using (var lidBrush = new SolidBrush(Color.FromArgb(255, 19, 21, 27)))
        {
            graphics.FillPath(lidBrush, lidPath);
        }

        var previousClip = graphics.Clip;
        using (var screenPath = RoundedRectangle(layout.ScreenBounds, Math.Max(7, layout.OuterPadding / 4)))
        {
            graphics.SetClip(screenPath);
            graphics.DrawImage(
                sourceImage,
                layout.ScreenBounds.Left,
                layout.ScreenBounds.Top,
                layout.ScreenBounds.Width,
                layout.ScreenBounds.Height);
        }
        graphics.Clip = previousClip;
        previousClip.Dispose();

        var cameraDiameter = Math.Max(5, layout.OuterPadding / 5);
        using (var cameraBrush = new SolidBrush(Accent))
        {
            graphics.FillEllipse(
                cameraBrush,
                layout.LidBounds.Left + layout.LidBounds.Width / 2 - cameraDiameter / 2,
                layout.LidBounds.Top + Math.Max(5, layout.ScreenBounds.Top - layout.LidBounds.Top) / 2 - cameraDiameter / 2,
                cameraDiameter,
                cameraDiameter);
        }

        using (var basePath = RoundedRectangle(layout.BaseBounds, Math.Max(8, layout.BaseBounds.Height / 4)))
        using (var baseBrush = new LinearGradientBrush(
            ToRectangle(layout.BaseBounds),
            Color.FromArgb(255, 225, 227, 235),
            Color.FromArgb(255, 124, 129, 147),
            LinearGradientMode.Vertical))
        {
            graphics.FillPath(baseBrush, basePath);
        }

        var notchWidth = Math.Max(18, layout.LidBounds.Width * 16 / 100);
        var notchHeight = Math.Max(6, layout.BaseBounds.Height / 7);
        var notch = new PixelRect(
            layout.CanvasWidth / 2 - notchWidth / 2,
            layout.BaseBounds.Top,
            notchWidth,
            notchHeight);
        using (var notchPath = RoundedRectangle(notch, notchHeight / 2))
        using (var notchBrush = new SolidBrush(Color.FromArgb(175, 86, 92, 255)))
        {
            graphics.FillPath(notchBrush, notchPath);
        }

        return Encode(canvas);
    }

    public CapturedImage ComposeSelectedWindows(
        IReadOnlyList<WindowCompositeItem> selectedWindows)
    {
        var layout = MultiWindowCompositeLayout.Create(selectedWindows);
        using var canvas = NewCanvas(layout.CanvasWidth, layout.CanvasHeight);
        using var graphics = Graphics.FromImage(canvas);
        Configure(graphics);
        graphics.Clear(Color.Transparent);

        // The catalog is in top-to-bottom z-order. Paint it in reverse so the
        // first selected top-level window remains visually on top.
        foreach (var placement in layout.Placements.Reverse())
        {
            DrawSoftShadow(graphics, placement.DestinationBounds, 14);
            using var stream = new MemoryStream(
                placement.Item.Image.CopyPngBytes(),
                writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: true);
            graphics.DrawImage(image, ToRectangle(placement.DestinationBounds));
            using var border = new Pen(Color.FromArgb(125, Accent), 2);
            graphics.DrawRectangle(
                border,
                placement.DestinationBounds.Left,
                placement.DestinationBounds.Top,
                placement.DestinationBounds.Width - 1,
                placement.DestinationBounds.Height - 1);
        }

        return Encode(canvas);
    }

    private static Bitmap NewCanvas(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(96, 96);
        return bitmap;
    }

    private static void Configure(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
    }

    private static void DrawSoftShadow(Graphics graphics, PixelRect bounds, int radius)
    {
        var layers = Math.Clamp(radius / 3, 4, 10);
        for (var layer = layers; layer >= 1; layer--)
        {
            var spread = layer * 2;
            var shadowBounds = new PixelRect(
                bounds.Left - spread,
                bounds.Top - spread + Math.Max(3, radius / 4),
                bounds.Width + spread * 2,
                bounds.Height + spread * 2);
            using var path = RoundedRectangle(shadowBounds, Math.Max(8, radius));
            using var brush = new SolidBrush(Color.FromArgb(Math.Max(3, 22 - layer * 2), 0, 0, 0));
            graphics.FillPath(brush, path);
        }
    }

    private static GraphicsPath RoundedRectangle(PixelRect bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(
            checked(radius * 2),
            Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 1)
        {
            path.AddRectangle(ToRectangle(bounds));
            return path;
        }

        var arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Rectangle ToRectangle(PixelRect bounds) =>
        new(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

    private static CapturedImage Encode(Bitmap bitmap)
    {
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return new CapturedImage(
            output.GetBuffer().AsSpan(0, checked((int)output.Length)),
            bitmap.Width,
            bitmap.Height);
    }
}
