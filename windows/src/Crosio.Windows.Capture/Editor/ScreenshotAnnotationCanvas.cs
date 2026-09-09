using System.Drawing;
using System.Drawing.Drawing2D;

namespace Crosio.Windows.Capture.Editor;

internal sealed class ScreenshotAnnotationCanvas : Control
{
    private readonly ScreenshotAnnotationDocument _document;
    private readonly List<ScreenshotPoint> _activePoints = [];
    private Bitmap _rendered;
    private ScreenshotPoint? _activeStart;
    private ScreenshotPoint? _activeEnd;

    internal ScreenshotAnnotationCanvas(ScreenshotAnnotationDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _rendered = document.RenderBitmap();
        BackColor = Color.FromArgb(31, 31, 34);
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        Name = "ScreenshotAnnotationCanvas";
        TabStop = true;
        AccessibleName = "截图标注画布";
        AccessibleDescription = "在截图上拖动以添加当前标注";
    }

    internal ScreenshotAnnotationTool Tool { get; set; } = ScreenshotAnnotationTool.Pen;

    internal Color AnnotationColor { get; set; } = Color.FromArgb(255, 255, 62, 70);

    internal float LineWidth { get; set; } = 6;

    internal float MosaicBrushDiameter { get; set; } = 40;

    internal int MosaicBlockSize { get; set; } = 10;

    internal event EventHandler? DocumentChanged;

    internal void RefreshFromDocument()
    {
        var replacement = _document.RenderBitmap();
        var previous = _rendered;
        _rendered = replacement;
        previous.Dispose();
        Invalidate();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rendered.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var destination = ScreenshotEditCoordinateMapper.ImageBounds(
            ClientSize,
            _document.SourcePixelSize);
        eventArgs.Graphics.CompositingQuality = CompositingQuality.HighQuality;
        eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.DrawImage(
            _rendered,
            destination,
            new RectangleF(0, 0, _rendered.Width, _rendered.Height),
            GraphicsUnit.Pixel);
        DrawActiveAnnotation(eventArgs.Graphics);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left
            || !TryMap(eventArgs.Location, out var sourcePoint))
        {
            return;
        }

        Focus();
        Capture = true;
        _activeStart = sourcePoint;
        _activeEnd = sourcePoint;
        _activePoints.Clear();
        _activePoints.Add(sourcePoint);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (_activeStart is null || !Capture)
        {
            return;
        }

        var sourcePoint = MapClamped(eventArgs.Location);
        _activeEnd = sourcePoint;
        if (Tool is ScreenshotAnnotationTool.Pen or ScreenshotAnnotationTool.Mosaic)
        {
            var previous = _activePoints[^1];
            var dx = sourcePoint.X - previous.X;
            var dy = sourcePoint.Y - previous.Y;
            if ((dx * dx) + (dy * dy) >= 1)
            {
                _activePoints.Add(sourcePoint);
            }
        }

        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button != MouseButtons.Left || _activeStart is null)
        {
            return;
        }

        var releasePoint = MapClamped(eventArgs.Location);
        _activeEnd = releasePoint;
        if (Tool is ScreenshotAnnotationTool.Pen or ScreenshotAnnotationTool.Mosaic
            && _activePoints[^1] != releasePoint)
        {
            _activePoints.Add(releasePoint);
        }

        var annotation = CreateActiveAnnotation();
        _activeStart = null;
        _activeEnd = null;
        _activePoints.Clear();
        Capture = false;
        if (annotation is not null)
        {
            _document.Add(annotation);
            RefreshFromDocument();
        }
        else
        {
            Invalidate();
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if (!Capture && MouseButtons == MouseButtons.None && _activeStart is not null)
        {
            _activeStart = null;
            _activeEnd = null;
            _activePoints.Clear();
            Invalidate();
        }
    }

    private ScreenshotAnnotation? CreateActiveAnnotation()
    {
        if (_activeStart is not { } start || _activeEnd is not { } end)
        {
            return null;
        }

        return Tool switch
        {
            ScreenshotAnnotationTool.Pen => new PenAnnotation(
                _activePoints,
                AnnotationColor.ToArgb(),
                LineWidth),
            ScreenshotAnnotationTool.Mosaic => new MosaicAnnotation(
                _activePoints,
                MosaicBrushDiameter,
                MosaicBlockSize),
            ScreenshotAnnotationTool.Rectangle => new RectangleAnnotation(
                start,
                end,
                AnnotationColor.ToArgb(),
                LineWidth),
            ScreenshotAnnotationTool.Arrow when Distance(start, end) >= 1 => new ArrowAnnotation(
                start,
                end,
                AnnotationColor.ToArgb(),
                LineWidth),
            _ => null,
        };
    }

    private void DrawActiveAnnotation(Graphics graphics)
    {
        if (_activeStart is not { } start || _activeEnd is not { } end)
        {
            return;
        }

        var bounds = ScreenshotEditCoordinateMapper.ImageBounds(
            ClientSize,
            _document.SourcePixelSize);
        var scale = bounds.Width / _document.SourcePixelSize.Width;
        using var pen = new Pen(AnnotationColor, Math.Max(1, LineWidth * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        switch (Tool)
        {
            case ScreenshotAnnotationTool.Pen:
                {
                    var points = _activePoints
                        .Select(point => ScreenshotEditCoordinateMapper.SourceToCanvas(
                            point,
                            ClientSize,
                            _document.SourcePixelSize))
                        .ToArray();
                    if (points.Length > 1)
                    {
                        graphics.DrawLines(pen, points);
                    }

                    break;
                }
            case ScreenshotAnnotationTool.Mosaic:
                {
                    pen.Color = System.Drawing.Color.FromArgb(180, 180, 180, 180);
                    pen.DashStyle = DashStyle.Dot;
                    pen.Width = Math.Max(2, MosaicBrushDiameter * scale);
                    var points = _activePoints
                        .Select(point => ScreenshotEditCoordinateMapper.SourceToCanvas(
                            point,
                            ClientSize,
                            _document.SourcePixelSize))
                        .ToArray();
                    if (points.Length == 1)
                    {
                        graphics.DrawEllipse(
                            pen,
                            points[0].X - (pen.Width / 2),
                            points[0].Y - (pen.Width / 2),
                            pen.Width,
                            pen.Width);
                    }
                    else
                    {
                        graphics.DrawLines(pen, points);
                    }

                    break;
                }
            case ScreenshotAnnotationTool.Rectangle:
                {
                    var first = ScreenshotEditCoordinateMapper.SourceToCanvas(
                        start,
                        ClientSize,
                        _document.SourcePixelSize);
                    var second = ScreenshotEditCoordinateMapper.SourceToCanvas(
                        end,
                        ClientSize,
                        _document.SourcePixelSize);
                    graphics.DrawRectangle(
                        pen,
                        Math.Min(first.X, second.X),
                        Math.Min(first.Y, second.Y),
                        Math.Abs(first.X - second.X),
                        Math.Abs(first.Y - second.Y));
                    break;
                }
            case ScreenshotAnnotationTool.Arrow:
                {
                    var first = ScreenshotEditCoordinateMapper.SourceToCanvas(
                        start,
                        ClientSize,
                        _document.SourcePixelSize);
                    var second = ScreenshotEditCoordinateMapper.SourceToCanvas(
                        end,
                        ClientSize,
                        _document.SourcePixelSize);
                    using var cap = new AdjustableArrowCap(
                        Math.Max(3, pen.Width * 2.5f),
                        Math.Max(4, pen.Width * 3.5f),
                        isFilled: true);
                    pen.CustomEndCap = cap;
                    graphics.DrawLine(pen, first, second);
                    break;
                }
        }
    }

    private bool TryMap(Point point, out ScreenshotPoint sourcePoint) =>
        ScreenshotEditCoordinateMapper.TryCanvasToSource(
            point,
            ClientSize,
            _document.SourcePixelSize,
            out sourcePoint);

    private ScreenshotPoint MapClamped(Point point)
    {
        var bounds = ScreenshotEditCoordinateMapper.ImageBounds(
            ClientSize,
            _document.SourcePixelSize);
        var clamped = new PointF(
            Math.Clamp(point.X, bounds.Left, bounds.Right - 0.01f),
            Math.Clamp(point.Y, bounds.Top, bounds.Bottom - 0.01f));
        _ = ScreenshotEditCoordinateMapper.TryCanvasToSource(
            clamped,
            ClientSize,
            _document.SourcePixelSize,
            out var sourcePoint);
        return sourcePoint;
    }

    private static double Distance(ScreenshotPoint first, ScreenshotPoint second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }
}
