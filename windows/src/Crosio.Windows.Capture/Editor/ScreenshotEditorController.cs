using System.Runtime.Versioning;
using Crosio.Windows.Capture.Clipboard;
using Crosio.Windows.Capture.Pinning;

namespace Crosio.Windows.Capture.Editor;

public sealed record ScreenshotEditorOptions(string? InitialStatus = null);

public sealed record ScreenshotEditorOpenResult(
    nint WindowHandle,
    bool OpenedNewWindow);

/// <summary>
/// Owns Crosio's single screenshot editor HWND on a dedicated STA message
/// loop. Showing another capture while the editor is already open focuses the
/// current editor and deliberately does not replace its image.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class ScreenshotEditorController : IDisposable
{
    private readonly IImageClipboard _clipboard;
    private readonly IPinnedScreenshotService _pins;
    private readonly IScreenshotEditorShareSink? _shareSink;
    private readonly IScreenshotEditorOcrService? _ocrService;
    private readonly IDisposable? _ownedPinManager;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly Thread _uiThread;
    private SynchronizationContext? _uiContext;
    private ScreenshotEditorWindow? _window;
    private Exception? _startupError;
    private int _disposed;

    public ScreenshotEditorController()
        : this(
            new WindowsImageClipboard(),
            new PinnedScreenshotManager(),
            shareSink: null,
            ocrService: null,
            ownsPinManager: true)
    {
    }

    public ScreenshotEditorController(
        IImageClipboard clipboard,
        IPinnedScreenshotService pins)
        : this(
            clipboard,
            pins,
            shareSink: null,
            ocrService: null,
            ownsPinManager: false)
    {
    }

    public ScreenshotEditorController(
        IImageClipboard clipboard,
        IPinnedScreenshotService pins,
        IScreenshotEditorShareSink? shareSink,
        IScreenshotEditorOcrService? ocrService)
        : this(
            clipboard,
            pins,
            shareSink,
            ocrService,
            ownsPinManager: false)
    {
    }

    private ScreenshotEditorController(
        IImageClipboard clipboard,
        IPinnedScreenshotService pins,
        IScreenshotEditorShareSink? shareSink,
        IScreenshotEditorOcrService? ocrService,
        bool ownsPinManager)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("The screenshot editor is only available on Windows.");
        }

        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _shareSink = shareSink;
        _ocrService = ocrService;
        _ownedPinManager = ownsPinManager ? pins as IDisposable : null;
        _uiThread = new Thread(RunUiLoop)
        {
            IsBackground = true,
            Name = "Crosio screenshot editor",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _ready.Wait();
        if (_startupError is not null)
        {
            _ownedPinManager?.Dispose();
            _ready.Dispose();
            throw new ScreenshotCaptureException(
                "The screenshot editor message loop could not start.",
                _startupError);
        }
    }

    public event EventHandler? EditorClosed;

    public Task<bool> ActivateExistingAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            if (_window is not { IsDisposed: false } current)
            {
                return false;
            }

            if (current.WindowState == FormWindowState.Minimized)
            {
                current.WindowState = FormWindowState.Normal;
            }
            current.Show();
            current.Activate();
            current.BringToFront();
            return true;
        }, cancellationToken);

    public Task<ScreenshotEditorOpenResult> ShowAsync(
        ScreenshotCaptureDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var status = delivery.WasCopiedToClipboard
            ? "截图已复制到剪贴板。"
            : "截图完成，但自动复制失败；可点击“复制图片”重试。";
        return ShowAsync(
            delivery.Image,
            new ScreenshotEditorOptions(status),
            cancellationToken);
    }

    public Task<ScreenshotEditorOpenResult> ShowAsync(
        CapturedImage image,
        ScreenshotEditorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new ScreenshotEditorOptions();
        return InvokeAsync(() =>
        {
            if (_window is { IsDisposed: false } current)
            {
                if (current.WindowState == FormWindowState.Minimized)
                {
                    current.WindowState = FormWindowState.Normal;
                }

                current.Show();
                current.Activate();
                current.BringToFront();
                return new ScreenshotEditorOpenResult(current.Handle, OpenedNewWindow: false);
            }

            var window = new ScreenshotEditorWindow(
                image,
                _clipboard,
                _pins,
                _shareSink,
                _ocrService,
                options.InitialStatus);
            window.FormClosed += OnEditorFormClosed;
            _window = window;
            window.Show();
            window.Activate();
            return new ScreenshotEditorOpenResult(window.Handle, OpenedNewWindow: true);
        }, cancellationToken);
    }

    public Task<bool> CloseAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync(() =>
        {
            if (_window is not { IsDisposed: false } current)
            {
                return false;
            }

            current.Close();
            return true;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var context = _uiContext;
        if (context is not null)
        {
            context.Post(_ =>
            {
                _window?.Close();
                Application.ExitThread();
            }, null);

            if (Thread.CurrentThread != _uiThread)
            {
                _ = _uiThread.Join(TimeSpan.FromSeconds(5));
            }
        }

        _ownedPinManager?.Dispose();
        _ready.Dispose();
    }

    private Task<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var context = _uiContext
            ?? throw new ScreenshotCaptureException("The screenshot editor message loop is unavailable.");
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(operation());
            }
            catch (OperationCanceledException cancelled)
            {
                completion.TrySetCanceled(cancelled.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }, null);
        return completion.Task;
    }

    private void RunUiLoop()
    {
        try
        {
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            _uiContext = context;
            _ready.Set();
            Application.Run();
        }
        catch (Exception error)
        {
            _startupError = error;
            _ready.Set();
        }
        finally
        {
            _window?.Dispose();
            _window = null;
        }
    }

    private void OnEditorFormClosed(object? sender, FormClosedEventArgs eventArgs)
    {
        if (sender is ScreenshotEditorWindow window)
        {
            window.FormClosed -= OnEditorFormClosed;
        }

        _window = null;
        EditorClosed?.Invoke(this, EventArgs.Empty);
    }
}
