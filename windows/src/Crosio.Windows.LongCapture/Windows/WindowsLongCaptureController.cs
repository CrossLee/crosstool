#if WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Crosio.Windows.Capture;
using Crosio.Windows.Capture.Selection;

namespace Crosio.Windows.LongCapture;

public sealed record WindowsLongCaptureResult(
    bool WasCancelled,
    CapturedImage? Image,
    string? Warning)
{
    public static WindowsLongCaptureResult Cancelled { get; } = new(true, null, null);
}

/// <summary>
/// Minimal interactive Windows long-capture flow: multi-monitor region picker,
/// non-activating capture-excluded blue border and controls, wheel-driven
/// sampling, manual up/down samples, and final eager PNG output.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsLongCaptureController : IDisposable
{
    private readonly ICaptureTargetPicker _picker;
    private readonly IScreenshotCaptureService _captureService;
    private readonly IDisposable? _ownedPicker;
    private int _sessionActive;
    private int _disposed;

    public WindowsLongCaptureController()
        : this(new WindowsCaptureTargetPicker(), new WindowsScreenshotCaptureService(), ownsPicker: true)
    {
    }

    public WindowsLongCaptureController(
        ICaptureTargetPicker picker,
        IScreenshotCaptureService captureService)
        : this(picker, captureService, ownsPicker: false)
    {
    }

    private WindowsLongCaptureController(
        ICaptureTargetPicker picker,
        IScreenshotCaptureService captureService,
        bool ownsPicker)
    {
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _ownedPicker = ownsPicker ? picker as IDisposable : null;
    }

    public async Task<WindowsLongCaptureResult> CaptureInteractiveAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _sessionActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("A 一爪 long-capture session is already running.");
        }

        try
        {
            var target = await _picker.PickRegionAsync(cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                return WindowsLongCaptureResult.Cancelled;
            }

            await using var session = await WindowsLongCaptureSession.StartAsync(
                target.Bounds,
                _captureService,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var image = await LongCaptureControlOverlay.RunAsync(
                session,
                target.Bounds,
                cancellationToken).ConfigureAwait(false);
            return image is null
                ? WindowsLongCaptureResult.Cancelled
                : new WindowsLongCaptureResult(false, image, session.LastWarning);
        }
        finally
        {
            Volatile.Write(ref _sessionActive, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ownedPicker?.Dispose();
        }
    }
}

internal static class LongCaptureControlOverlay
{
    internal static Task<CapturedImage?> RunAsync(
        WindowsLongCaptureSession session,
        PixelRect region,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<CapturedImage?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunUi(session, region, cancellationToken, completion))
        {
            IsBackground = true,
            Name = "Crosio long-capture controls",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunUi(
        WindowsLongCaptureSession session,
        PixelRect region,
        CancellationToken cancellationToken,
        TaskCompletionSource<CapturedImage?> completion)
    {
        try
        {
            using var border = new LongCaptureBorderWindow(region);
            using var toolbar = new LongCaptureToolbarWindow(session, region);
            _ = border.Handle;
            _ = toolbar.Handle;
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (toolbar.IsHandleCreated && !toolbar.IsDisposed)
                    {
                        toolbar.BeginInvoke(toolbar.CancelAndClose);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            });
            border.Show();
            toolbar.Show();
            if (!border.IsExcludedFromCapture || !toolbar.IsExcludedFromCapture)
            {
                throw new InvalidOperationException(
                    "Windows could not exclude the long-capture controls from captured pixels.");
            }

            Application.Run(toolbar);
            border.Close();
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetResult(toolbar.Result);
            }
        }
        catch (OperationCanceledException cancelled)
        {
            completion.TrySetCanceled(cancelled.CancellationToken);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }
}

internal sealed class LongCaptureBorderWindow : Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    internal LongCaptureBorderWindow(PixelRect region)
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Magenta;
        Bounds = new Rectangle(region.Left, region.Top, region.Width, region.Height);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        Name = "CrosioLongCaptureBorder";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        TransparencyKey = Color.Magenta;
    }

    internal bool IsExcludedFromCapture { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        IsExcludedFromCapture = LongCaptureWindowNative.SetWindowDisplayAffinity(
            Handle,
            LongCaptureWindowNative.WdaExcludeFromCapture);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using var pen = new Pen(Color.FromArgb(255, 65, 105, 255), 4);
        eventArgs.Graphics.DrawRectangle(
            pen,
            2,
            2,
            Math.Max(0, ClientSize.Width - 5),
            Math.Max(0, ClientSize.Height - 5));
    }
}

internal sealed class LongCaptureToolbarWindow : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly WindowsLongCaptureSession _session;
    private readonly Label _status;
    private readonly Button _upButton;
    private readonly Button _downButton;
    private readonly Button _finishButton;
    private readonly Button _cancelButton;
    private bool _busy;

    internal LongCaptureToolbarWindow(WindowsLongCaptureSession session, PixelRect region)
    {
        _session = session;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(248, 248, 250);
        ClientSize = new Size(500, 54);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "CrosioLongCaptureToolbar";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "长截图 — 一爪";
        TopMost = true;
        AccessibleName = "一爪长截图控制条";
        AccessibleDescription = "显示当前拼接高度，并可向上或向下补采、完成或取消";

        _status = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(62, 62, 66),
            Name = "LongCaptureStatus",
            Padding = new Padding(8, 0, 4, 0),
            Text = CurrentSizeText(),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _upButton = CreateButton("向上补采", 78);
        _downButton = CreateButton("向下补采", 78);
        _finishButton = CreateButton("完成", 64);
        _cancelButton = CreateButton("取消", 64);
        _upButton.Click += OnUpClicked;
        _downButton.Click += OnDownClicked;
        _finishButton.Click += OnFinishClicked;
        _cancelButton.Click += OnCancelClicked;

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = new Padding(0, 7, 6, 0),
            WrapContents = false,
        };
        buttons.Controls.Add(_upButton);
        buttons.Controls.Add(_downButton);
        buttons.Controls.Add(_finishButton);
        buttons.Controls.Add(_cancelButton);

        var root = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(buttons, 1, 0);
        Controls.Add(root);
        Bounds = LongCaptureToolbarLayout.Place(
            region,
            Size,
            Cursor.Position,
            Screen.AllScreens.Select(screen => screen.WorkingArea));
        _session.FrameProcessed += OnFrameProcessed;
    }

    internal CapturedImage? Result { get; private set; }

    internal bool IsExcludedFromCapture { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.FrameProcessed -= OnFrameProcessed;
            _upButton.Click -= OnUpClicked;
            _downButton.Click -= OnDownClicked;
            _finishButton.Click -= OnFinishClicked;
            _cancelButton.Click -= OnCancelClicked;
        }

        base.Dispose(disposing);
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        IsExcludedFromCapture = LongCaptureWindowNative.SetWindowDisplayAffinity(
            Handle,
            LongCaptureWindowNative.WdaExcludeFromCapture);
    }

    internal void CancelAndClose()
    {
        if (!IsDisposed)
        {
            Result = null;
            Close();
        }
    }

    private static Button CreateButton(string text, int width) => new()
    {
        AutoSize = true,
        MinimumSize = new Size(width, 34),
        Padding = new Padding(5, 2, 5, 2),
        Text = text,
        UseVisualStyleBackColor = true,
    };

    private void OnUpClicked(object? sender, EventArgs eventArgs) =>
        _ = CaptureManualAsync(VerticalScrollDirection.Up);

    private void OnDownClicked(object? sender, EventArgs eventArgs) =>
        _ = CaptureManualAsync(VerticalScrollDirection.Down);

    private void OnFinishClicked(object? sender, EventArgs eventArgs) => _ = FinishAsync();

    private void OnCancelClicked(object? sender, EventArgs eventArgs) => CancelAndClose();

    private async Task CaptureManualAsync(VerticalScrollDirection direction)
    {
        if (!TryBeginBusy("正在补采…"))
        {
            return;
        }

        try
        {
            await _session.CaptureManuallyAsync(direction);
            _status.Text = _session.LastWarning ?? CurrentSizeText();
        }
        catch (Exception error)
        {
            _status.Text = $"补采失败：{error.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task FinishAsync()
    {
        if (!TryBeginBusy("正在补末帧…"))
        {
            return;
        }

        try
        {
            Result = await _session.CompleteAsync();
            if (!IsDisposed)
            {
                Close();
            }
        }
        catch (Exception error)
        {
            _status.Text = $"完成失败：{error.Message}";
            EndBusy();
        }
    }

    private void OnFrameProcessed(object? sender, LongCaptureFrameProcessedEventArgs eventArgs)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed)
                    {
                        _status.Text = eventArgs.Warning ?? CurrentSizeText();
                    }
                });
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool TryBeginBusy(string status)
    {
        if (_busy || IsDisposed)
        {
            return false;
        }

        _busy = true;
        _status.Text = status;
        SetButtonsEnabled(false);
        return true;
    }

    private void EndBusy()
    {
        if (!IsDisposed)
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _upButton.Enabled = enabled;
        _downButton.Enabled = enabled;
        _finishButton.Enabled = enabled;
        _cancelButton.Enabled = enabled;
    }

    private string CurrentSizeText() =>
        $"当前 {_session.OutputWidth} × {_session.OutputHeight}";
}

internal static class LongCaptureToolbarLayout
{
    internal static Rectangle Place(
        PixelRect selection,
        Size toolbarSize,
        Point anchor,
        IEnumerable<Rectangle> workingAreas)
    {
        var areas = workingAreas.ToArray();
        var area = areas.FirstOrDefault(candidate => candidate.Contains(anchor));
        if (area.Width <= 0 || area.Height <= 0)
        {
            area = areas.FirstOrDefault(candidate => candidate.IntersectsWith(
                new Rectangle(selection.Left, selection.Top, selection.Width, selection.Height)));
        }

        if (area.Width <= 0 || area.Height <= 0)
        {
            area = new Rectangle(selection.Left, selection.Top, selection.Width, selection.Height);
        }

        const int gap = 10;
        var candidates = new[]
        {
            new Point(selection.Right - toolbarSize.Width, selection.Bottom + gap),
            new Point(selection.Right - toolbarSize.Width, selection.Top - toolbarSize.Height - gap),
            new Point(selection.Left, selection.Bottom + gap),
            new Point(selection.Left, selection.Top - toolbarSize.Height - gap),
        };
        var fitting = candidates
            .Where(point => area.Contains(new Rectangle(point, toolbarSize)))
            .OrderBy(point => DistanceSquared(point, anchor))
            .Select(static point => (Point?)point)
            .FirstOrDefault();
        if (fitting is { } fittingPoint)
        {
            return new Rectangle(fittingPoint, toolbarSize);
        }

        var x = Math.Clamp(anchor.X - (toolbarSize.Width / 2), area.Left, area.Right - toolbarSize.Width);
        var y = Math.Clamp(anchor.Y + gap, area.Top, area.Bottom - toolbarSize.Height);
        return new Rectangle(x, y, toolbarSize.Width, toolbarSize.Height);
    }

    private static long DistanceSquared(Point first, Point second)
    {
        var deltaX = (long)first.X - second.X;
        var deltaY = (long)first.Y - second.Y;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }
}

internal static class LongCaptureWindowNative
{
    internal const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);
}
#endif
