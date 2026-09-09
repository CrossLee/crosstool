using System.Drawing;
using System.Drawing.Drawing2D;

namespace Crosio.Windows.Capture.Selection;

internal enum CaptureSelectionOverlayMode
{
    Point,
    Region,
}

internal sealed record CaptureSelectionOverlayResult(
    Point? ScreenPoint,
    PixelRect? Region);

internal sealed class CaptureSelectionOverlay : Form
{
    private const int WsExToolWindow = 0x00000080;
    private readonly CaptureSelectionOverlayMode _mode;
    private Point? _dragStart;
    private Point? _dragCurrent;

    internal CaptureSelectionOverlay(
        CaptureSelectionOverlayMode mode,
        Rectangle virtualDesktopBounds)
    {
        _mode = mode;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        Bounds = virtualDesktopBounds;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        Name = "CrosioCaptureSelectionOverlay";
        Opacity = 0.32;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = mode == CaptureSelectionOverlayMode.Region
            ? "选择截图区域 — 一爪"
            : "选择截图目标 — 一爪";
        TopMost = true;
        AccessibleName = Text;
        AccessibleDescription = mode == CaptureSelectionOverlayMode.Region
            ? "拖动鼠标选择截图区域，按 Esc 或右键取消"
            : "单击要截图的显示器或窗口，按 Esc 或右键取消";
    }

    internal CaptureSelectionOverlayResult? Result { get; private set; }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        Activate();
        Focus();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var instruction = _mode == CaptureSelectionOverlayMode.Region
            ? "拖动选择截图区域 · Esc 或右键取消"
            : "单击选择目标 · Esc 或右键取消";
        using var font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold);
        var textSize = eventArgs.Graphics.MeasureString(instruction, font);
        var textBounds = new RectangleF(
            (ClientSize.Width - textSize.Width) / 2 - 18,
            28,
            textSize.Width + 36,
            textSize.Height + 20);
        using var textBackground = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
        using var textBrush = new SolidBrush(Color.White);
        eventArgs.Graphics.FillRoundedRectangle(textBackground, textBounds, 10);
        eventArgs.Graphics.DrawString(
            instruction,
            font,
            textBrush,
            textBounds.Left + 18,
            textBounds.Top + 10);

        if (_mode != CaptureSelectionOverlayMode.Region
            || _dragStart is null
            || _dragCurrent is null)
        {
            return;
        }

        var selection = ClientRectangleFromEndpoints(_dragStart.Value, _dragCurrent.Value);
        if (selection.Width <= 0 || selection.Height <= 0)
        {
            return;
        }

        using var selectedFill = new SolidBrush(Color.FromArgb(45, 86, 92, 255));
        using var selectedBorder = new Pen(Color.FromArgb(255, 96, 102, 255), 3);
        eventArgs.Graphics.FillRectangle(selectedFill, selection);
        eventArgs.Graphics.DrawRectangle(selectedBorder, selection);

        var sizeLabel = $"{selection.Width} × {selection.Height}";
        using var labelFont = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        var labelSize = eventArgs.Graphics.MeasureString(sizeLabel, labelFont);
        var labelBounds = new RectangleF(
            selection.Left,
            Math.Max(0, selection.Top - labelSize.Height - 12),
            labelSize.Width + 16,
            labelSize.Height + 8);
        eventArgs.Graphics.FillRectangle(textBackground, labelBounds);
        eventArgs.Graphics.DrawString(
            sizeLabel,
            labelFont,
            textBrush,
            labelBounds.Left + 8,
            labelBounds.Top + 4);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button == MouseButtons.Right)
        {
            CancelSelection();
            return;
        }

        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        if (_mode == CaptureSelectionOverlayMode.Point)
        {
            Result = new CaptureSelectionOverlayResult(PointToScreen(eventArgs.Location), null);
            Close();
            return;
        }

        _dragStart = PointToScreen(eventArgs.Location);
        _dragCurrent = _dragStart;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (_mode != CaptureSelectionOverlayMode.Region || _dragStart is null)
        {
            return;
        }

        _dragCurrent = PointToScreen(eventArgs.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (_mode != CaptureSelectionOverlayMode.Region
            || eventArgs.Button != MouseButtons.Left
            || _dragStart is null)
        {
            return;
        }

        Capture = false;
        _dragCurrent = PointToScreen(eventArgs.Location);
        var region = CaptureSelectionGeometry.RegionFromEndpoints(
            _dragStart.Value,
            _dragCurrent.Value);
        if (region is null)
        {
            _dragStart = null;
            _dragCurrent = null;
            Invalidate();
            return;
        }

        Result = new CaptureSelectionOverlayResult(null, region);
        Close();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            CancelSelection();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    internal void CancelSelection()
    {
        Result = null;
        Close();
    }

    private Rectangle ClientRectangleFromEndpoints(Point firstScreenPoint, Point secondScreenPoint)
    {
        var first = PointToClient(firstScreenPoint);
        var second = PointToClient(secondScreenPoint);
        return Rectangle.FromLTRB(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Max(first.X, second.X),
            Math.Max(first.Y, second.Y));
    }
}

internal static class GraphicsRoundedRectangleExtensions
{
    internal static void FillRoundedRectangle(
        this Graphics graphics,
        Brush brush,
        RectangleF rectangle,
        float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
