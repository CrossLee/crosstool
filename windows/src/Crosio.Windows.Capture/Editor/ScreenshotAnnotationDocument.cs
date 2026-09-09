using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Crosio.Windows.Capture.Editor;

public enum ScreenshotAnnotationTool
{
    Pen,
    Mosaic,
    Rectangle,
    Arrow,
}

public readonly record struct ScreenshotPoint(float X, float Y);

public abstract record ScreenshotAnnotation;

public sealed record PenAnnotation : ScreenshotAnnotation
{
    public PenAnnotation(IEnumerable<ScreenshotPoint> points, int argb, float width)
    {
        Points = CopyPoints(points, minimumCount: 1);
        if (!float.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Argb = argb;
        Width = width;
    }

    public IReadOnlyList<ScreenshotPoint> Points { get; }

    public int Argb { get; }

    public float Width { get; }

    private static IReadOnlyList<ScreenshotPoint> CopyPoints(
        IEnumerable<ScreenshotPoint> points,
        int minimumCount)
    {
        var copy = (points ?? throw new ArgumentNullException(nameof(points))).ToArray();
        if (copy.Length < minimumCount)
        {
            throw new ArgumentException("The annotation does not contain enough points.", nameof(points));
        }

        if (copy.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            throw new ArgumentException("Annotation points must be finite.", nameof(points));
        }

        return new ReadOnlyCollection<ScreenshotPoint>(copy);
    }
}

public sealed record MosaicAnnotation : ScreenshotAnnotation
{
    public MosaicAnnotation(
        IEnumerable<ScreenshotPoint> points,
        float brushDiameter,
        int blockSize)
    {
        var copy = (points ?? throw new ArgumentNullException(nameof(points))).ToArray();
        if (copy.Length == 0
            || copy.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            throw new ArgumentException("The mosaic path must contain finite points.", nameof(points));
        }

        if (!float.IsFinite(brushDiameter) || brushDiameter <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(brushDiameter));
        }

        if (blockSize <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        Points = new ReadOnlyCollection<ScreenshotPoint>(copy);
        BrushDiameter = brushDiameter;
        BlockSize = blockSize;
    }

    public IReadOnlyList<ScreenshotPoint> Points { get; }

    public float BrushDiameter { get; }

    public int BlockSize { get; }
}

public sealed record RectangleAnnotation : ScreenshotAnnotation
{
    public RectangleAnnotation(
        ScreenshotPoint start,
        ScreenshotPoint end,
        int argb,
        float width)
    {
        ValidatePoint(start, nameof(start));
        ValidatePoint(end, nameof(end));
        if (!float.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Start = start;
        End = end;
        Argb = argb;
        Width = width;
    }

    public ScreenshotPoint Start { get; }

    public ScreenshotPoint End { get; }

    public int Argb { get; }

    public float Width { get; }

    private static void ValidatePoint(ScreenshotPoint point, string parameterName)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            throw new ArgumentException("Annotation points must be finite.", parameterName);
        }
    }
}

public sealed record ArrowAnnotation : ScreenshotAnnotation
{
    public ArrowAnnotation(
        ScreenshotPoint start,
        ScreenshotPoint end,
        int argb,
        float width)
    {
        if (!float.IsFinite(start.X)
            || !float.IsFinite(start.Y)
            || !float.IsFinite(end.X)
            || !float.IsFinite(end.Y))
        {
            throw new ArgumentException("Annotation points must be finite.");
        }

        if (!float.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Start = start;
        End = end;
        Argb = argb;
        Width = width;
    }

    public ScreenshotPoint Start { get; }

    public ScreenshotPoint End { get; }

    public int Argb { get; }

    public float Width { get; }
}

public sealed record ScreenshotEditSnapshot(CapturedImage Image, int Version);

/// <summary>
/// Full-resolution screenshot plus immutable source-pixel annotations. Every
/// history mutation increments Version, allowing delayed output work to avoid
/// closing a newer editor state.
/// </summary>
public sealed class ScreenshotAnnotationDocument : IDisposable
{
    private readonly Bitmap _source;
    private readonly List<ScreenshotAnnotation> _annotations = [];
    private readonly Stack<IReadOnlyList<ScreenshotAnnotation>> _undo = [];
    private readonly Stack<IReadOnlyList<ScreenshotAnnotation>> _redo = [];
    private int _disposed;

    public ScreenshotAnnotationDocument(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            using var stream = new MemoryStream(image.CopyPngBytes(), writable: false);
            using var decoded = Image.FromStream(
                stream,
                useEmbeddedColorManagement: true,
                validateImageData: true);
            _source = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
            _source.SetResolution(96, 96);
            using var graphics = Graphics.FromImage(_source);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(decoded, 0, 0);
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException)
        {
            throw new ScreenshotCaptureException("The screenshot could not be decoded for editing.", error);
        }
    }

    public Size SourcePixelSize => _source.Size;

    public int Version { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool HasAnnotations => _annotations.Count > 0;

    public IReadOnlyList<ScreenshotAnnotation> Annotations =>
        new ReadOnlyCollection<ScreenshotAnnotation>(_annotations.ToArray());

    public void Add(ScreenshotAnnotation annotation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(annotation);
        _undo.Push(_annotations.ToArray());
        _redo.Clear();
        _annotations.Add(annotation);
        Version++;
    }

    public bool Undo()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Push(_annotations.ToArray());
        ReplaceAnnotations(_undo.Pop());
        Version++;
        return true;
    }

    public bool Redo()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Push(_annotations.ToArray());
        ReplaceAnnotations(_redo.Pop());
        Version++;
        return true;
    }

    public bool Reset()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_annotations.Count == 0)
        {
            return false;
        }

        _undo.Push(_annotations.ToArray());
        _redo.Clear();
        _annotations.Clear();
        Version++;
        return true;
    }

    public Bitmap RenderBitmap()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return ScreenshotAnnotationRenderer.Render(_source, _annotations);
    }

    public ScreenshotEditSnapshot CreateOutputSnapshot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var version = Version;
        using var bitmap = RenderBitmap();
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        var image = new CapturedImage(
            output.GetBuffer().AsSpan(0, checked((int)output.Length)),
            bitmap.Width,
            bitmap.Height);
        return new ScreenshotEditSnapshot(image, version);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source.Dispose();
        }
    }

    private void ReplaceAnnotations(IEnumerable<ScreenshotAnnotation> annotations)
    {
        _annotations.Clear();
        _annotations.AddRange(annotations);
    }
}

internal static class ScreenshotAnnotationRenderer
{
    internal static Bitmap Render(
        Bitmap source,
        IEnumerable<ScreenshotAnnotation> annotations)
    {
        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        output.SetResolution(96, 96);
        using (var graphics = Graphics.FromImage(output))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        foreach (var annotation in annotations)
        {
            switch (annotation)
            {
                case PenAnnotation pen:
                    DrawPen(output, pen);
                    break;
                case MosaicAnnotation mosaic:
                    DrawMosaic(output, mosaic);
                    break;
                case RectangleAnnotation rectangle:
                    DrawRectangle(output, rectangle);
                    break;
                case ArrowAnnotation arrow:
                    DrawArrow(output, arrow);
                    break;
            }
        }

        return output;
    }

    private static void DrawPen(Bitmap bitmap, PenAnnotation annotation)
    {
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);
        using var pen = CreatePen(annotation.Argb, annotation.Width);
        var points = annotation.Points.Select(ToPointF).ToArray();
        if (points.Length == 1)
        {
            using var brush = new SolidBrush(Color.FromArgb(annotation.Argb));
            var radius = annotation.Width / 2;
            graphics.FillEllipse(
                brush,
                points[0].X - radius,
                points[0].Y - radius,
                annotation.Width,
                annotation.Width);
        }
        else
        {
            graphics.DrawLines(pen, points);
        }
    }

    private static void DrawRectangle(Bitmap bitmap, RectangleAnnotation annotation)
    {
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);
        using var pen = CreatePen(annotation.Argb, annotation.Width);
        var bounds = RectangleF.FromLTRB(
            Math.Min(annotation.Start.X, annotation.End.X),
            Math.Min(annotation.Start.Y, annotation.End.Y),
            Math.Max(annotation.Start.X, annotation.End.X),
            Math.Max(annotation.Start.Y, annotation.End.Y));
        graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static void DrawArrow(Bitmap bitmap, ArrowAnnotation annotation)
    {
        var deltaX = annotation.End.X - annotation.Start.X;
        var deltaY = annotation.End.Y - annotation.Start.Y;
        var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (length < 1)
        {
            return;
        }

        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);
        using var pen = CreatePen(annotation.Argb, annotation.Width);
        using var arrowCap = new AdjustableArrowCap(
            Math.Max(3, annotation.Width * 2.5f),
            Math.Max(4, annotation.Width * 3.5f),
            isFilled: true);
        pen.CustomEndCap = arrowCap;
        graphics.DrawLine(pen, ToPointF(annotation.Start), ToPointF(annotation.End));
    }

    private static void DrawMosaic(Bitmap bitmap, MosaicAnnotation annotation)
    {
        var centers = Interpolate(annotation.Points, Math.Max(1, annotation.BrushDiameter / 4));
        var radius = annotation.BrushDiameter / 2;
        var blockSize = annotation.BlockSize;
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        foreach (var center in centers)
        {
            var left = Math.Max(0, (int)Math.Floor((center.X - radius) / blockSize) * blockSize);
            var top = Math.Max(0, (int)Math.Floor((center.Y - radius) / blockSize) * blockSize);
            var right = Math.Min(bitmap.Width, (int)Math.Ceiling((center.X + radius) / blockSize) * blockSize);
            var bottom = Math.Min(bitmap.Height, (int)Math.Ceiling((center.Y + radius) / blockSize) * blockSize);
            for (var y = top; y < bottom; y += blockSize)
            {
                for (var x = left; x < right; x += blockSize)
                {
                    var middleX = Math.Min(bitmap.Width - 1, x + (blockSize / 2));
                    var middleY = Math.Min(bitmap.Height - 1, y + (blockSize / 2));
                    var dx = middleX - center.X;
                    var dy = middleY - center.Y;
                    if ((dx * dx) + (dy * dy) > radius * radius)
                    {
                        continue;
                    }

                    using var brush = new SolidBrush(bitmap.GetPixel(middleX, middleY));
                    graphics.FillRectangle(
                        brush,
                        x,
                        y,
                        Math.Min(blockSize, bitmap.Width - x),
                        Math.Min(blockSize, bitmap.Height - y));
                }
            }
        }
    }

    private static IEnumerable<ScreenshotPoint> Interpolate(
        IReadOnlyList<ScreenshotPoint> points,
        float step)
    {
        yield return points[0];
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            var count = Math.Max(1, (int)Math.Ceiling(distance / step));
            for (var part = 1; part <= count; part++)
            {
                var fraction = part / (float)count;
                yield return new ScreenshotPoint(
                    start.X + (deltaX * fraction),
                    start.Y + (deltaY * fraction));
            }
        }
    }

    private static Pen CreatePen(int argb, float width) => new(Color.FromArgb(argb), width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
    }

    private static PointF ToPointF(ScreenshotPoint point) => new(point.X, point.Y);
}
