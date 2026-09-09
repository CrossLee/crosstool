using System.Drawing;
using System.Drawing.Drawing2D;
using Crosio.Windows.Capture.Interop;

namespace Crosio.Windows.Capture.Pinning;

internal sealed class PinnedScreenshotWindow : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const long PreviousKeyStateMask = 1L << 30;

    private readonly PinnedScreenshotSnapshot _snapshot;
    private bool _isHovered;
    private bool _isDragging;
    private Point _dragStartCursor;
    private Point _dragStartOrigin;

    internal PinnedScreenshotWindow(
        PinnedScreenshotSnapshot snapshot,
        Rectangle initialBounds)
    {
        _snapshot = snapshot;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        Bounds = initialBounds;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "CrosioPinnedScreenshot";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "固定截图 — 一爪";
        TopMost = true;

        AccessibleName = "固定的截图";
        AccessibleDescription = "拖动可移动贴图，滚动鼠标滚轮可放大或缩小，按 Esc 可关闭";
    }

    internal bool IsExcludedFromCapture { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        IsExcludedFromCapture = NativeMethods.SetWindowDisplayAffinity(
            Handle,
            NativeMethods.WdaExcludeFromCapture);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.CompositingMode = CompositingMode.SourceCopy;
        eventArgs.Graphics.CompositingQuality = CompositingQuality.HighQuality;
        eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        eventArgs.Graphics.DrawImage(
            _snapshot.Bitmap,
            ClientRectangle,
            0,
            0,
            _snapshot.Bitmap.Width,
            _snapshot.Bitmap.Height,
            GraphicsUnit.Pixel);

        eventArgs.Graphics.CompositingMode = CompositingMode.SourceOver;

        using var borderPen = new Pen(Color.FromArgb(150, 110, 110, 110), Math.Max(1, DeviceDpi / 96f));
        eventArgs.Graphics.DrawRectangle(
            borderPen,
            0,
            0,
            Math.Max(0, ClientSize.Width - 1),
            Math.Max(0, ClientSize.Height - 1));

        if (_isHovered)
        {
            DrawCloseButton(eventArgs.Graphics);
        }
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        base.OnMouseEnter(eventArgs);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        if (ClientRectangle.Contains(PointToClient(Control.MousePosition)))
        {
            return;
        }

        _isHovered = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        if (_isHovered && CloseButtonBounds.Contains(eventArgs.Location))
        {
            Close();
            return;
        }

        _isDragging = true;
        _dragStartCursor = Control.MousePosition;
        _dragStartOrigin = Bounds.Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        Cursor = _isHovered && CloseButtonBounds.Contains(eventArgs.Location)
            ? Cursors.Hand
            : Cursors.SizeAll;
        if (!_isDragging)
        {
            return;
        }

        var cursor = Control.MousePosition;
        var proposed = new RectangleF(
            _dragStartOrigin.X + cursor.X - _dragStartCursor.X,
            _dragStartOrigin.Y + cursor.Y - _dragStartCursor.Y,
            Width,
            Height);
        var workingArea = Screen.FromPoint(cursor).WorkingArea;
        ApplyBounds(PinnedScreenshotLayout.Clamp(proposed, workingArea));
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button == MouseButtons.Left)
        {
            _isDragging = false;
            Capture = false;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs eventArgs)
    {
        base.OnMouseWheel(eventArgs);
        var scaleFactor = PinnedScreenshotLayout.ScaleFactorForWheel(
            deltaX: 0,
            deltaY: eventArgs.Delta,
            hasPreciseDeltas: false,
            hasMomentum: false);
        if (scaleFactor is null)
        {
            return;
        }

        var cursor = Control.MousePosition;
        var screen = Screen.FromPoint(cursor);
        var workingArea = (RectangleF)screen.WorkingArea;
        var dpiScale = Math.Max(1, DeviceDpi / 96f);
        var adjusted = PinnedScreenshotLayout.ZoomedBounds(
            Bounds,
            cursor,
            scaleFactor.Value,
            PinnedScreenshotLayout.MinimumSize(_snapshot.SourcePixelSize, dpiScale),
            PinnedScreenshotLayout.MaximumSize(workingArea),
            workingArea);
        ApplyBounds(adjusted);
    }

    protected override void WndProc(ref Message message)
    {
        if ((message.Msg == WmKeyDown || message.Msg == WmSysKeyDown)
            && message.WParam.ToInt32() == PinnedScreenshotShortcut.VirtualKeyEscape)
        {
            var modifiers = CurrentModifiers();
            var isRepeat = (message.LParam.ToInt64() & PreviousKeyStateMask) != 0;
            var decision = PinnedScreenshotShortcut.DecidePinnedWindowClose(
                PinnedScreenshotShortcut.VirtualKeyEscape,
                modifiers,
                isRepeat,
                ReferenceEquals(ActiveForm, this) || ContainsFocus || Focused);
            if (decision == ScreenshotShortcutDecision.Consume)
            {
                return;
            }

            if (decision == ScreenshotShortcutDecision.ClosePin)
            {
                BeginInvoke(Close);
                return;
            }
        }

        base.WndProc(ref message);
    }

    private Rectangle CloseButtonBounds
    {
        get
        {
            var scale = Math.Max(1, DeviceDpi / 96f);
            var size = Math.Max(18, (int)Math.Round(24 * scale));
            var margin = Math.Max(4, (int)Math.Round(7 * scale));
            return new Rectangle(
                Math.Max(0, ClientSize.Width - size - margin),
                margin,
                size,
                size);
        }
    }

    private void DrawCloseButton(Graphics graphics)
    {
        var bounds = CloseButtonBounds;
        using var background = new SolidBrush(Color.FromArgb(165, 0, 0, 0));
        graphics.FillEllipse(background, bounds);
        var inset = Math.Max(5, bounds.Width / 4);
        var stroke = Math.Max(1.5f, bounds.Width / 12f);
        using var pen = new Pen(Color.White, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(
            pen,
            bounds.Left + inset,
            bounds.Top + inset,
            bounds.Right - inset,
            bounds.Bottom - inset);
        graphics.DrawLine(
            pen,
            bounds.Right - inset,
            bounds.Top + inset,
            bounds.Left + inset,
            bounds.Bottom - inset);
    }

    private void ApplyBounds(RectangleF bounds)
    {
        SetBounds(
            (int)Math.Round(bounds.Left),
            (int)Math.Round(bounds.Top),
            Math.Max(1, (int)Math.Round(bounds.Width)),
            Math.Max(1, (int)Math.Round(bounds.Height)));
    }

    private static ScreenshotShortcutModifiers CurrentModifiers()
    {
        var keys = ModifierKeys;
        var result = ScreenshotShortcutModifiers.None;
        if ((keys & Keys.Control) != 0)
        {
            result |= ScreenshotShortcutModifiers.Control;
        }

        if ((keys & Keys.Alt) != 0)
        {
            result |= ScreenshotShortcutModifiers.Alt;
        }

        if ((keys & Keys.Shift) != 0)
        {
            result |= ScreenshotShortcutModifiers.Shift;
        }

        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VkLeftWindows) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(NativeMethods.VkRightWindows) & 0x8000) != 0)
        {
            result |= ScreenshotShortcutModifiers.Windows;
        }

        return result;
    }
}
