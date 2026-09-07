using System.Drawing;

namespace Crosio.Windows.Capture.Selection;

/// <summary>
/// Native topmost selection overlays for display, window, and fixed-region
/// screenshots. Escape and right-click return null; cancellation tokens remain
/// cancellation rather than being reinterpreted as a capture.
/// </summary>
public sealed class WindowsCaptureTargetPicker : ICaptureTargetPicker, IDisposable
{
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly ICaptureTargetCatalog _catalog;
    private int _disposed;

    public WindowsCaptureTargetPicker(ICaptureTargetCatalog? catalog = null)
    {
        _catalog = catalog ?? new WindowsCaptureTargetCatalog();
    }

    public async Task<DisplayCaptureTarget?> PickDisplayAsync(
        CancellationToken cancellationToken = default)
    {
        var selection = await SelectAsync(
            CaptureSelectionOverlayMode.Point,
            cancellationToken).ConfigureAwait(false);
        if (selection?.ScreenPoint is not Point point)
        {
            return null;
        }

        var screen = Screen.AllScreens.FirstOrDefault(candidate => candidate.Bounds.Contains(point));
        return screen is null
            ? null
            : new DisplayCaptureTarget(
                screen.DeviceName,
                new PixelRect(
                    screen.Bounds.Left,
                    screen.Bounds.Top,
                    screen.Bounds.Width,
                    screen.Bounds.Height));
    }

    public async Task<WindowCaptureTarget?> PickWindowAsync(
        CancellationToken cancellationToken = default)
    {
        var selection = await SelectAsync(
            CaptureSelectionOverlayMode.Point,
            cancellationToken).ConfigureAwait(false);
        if (selection?.ScreenPoint is not Point point)
        {
            return null;
        }

        var processId = checked((uint)Environment.ProcessId);
        var selectedWindow = _catalog
            .GetSnapshot(processId)
            .Windows
            .FirstOrDefault(window => Contains(window.Bounds, point));
        return selectedWindow is null
            ? null
            : new WindowCaptureTarget(selectedWindow.WindowHandle);
    }

    public async Task<RegionCaptureTarget?> PickRegionAsync(
        CancellationToken cancellationToken = default)
    {
        var selection = await SelectAsync(
            CaptureSelectionOverlayMode.Region,
            cancellationToken).ConfigureAwait(false);
        return selection?.Region is PixelRect region
            ? new RegionCaptureTarget(region)
            : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _selectionGate.Dispose();
        }
    }

    private async Task<CaptureSelectionOverlayResult?> SelectAsync(
        CaptureSelectionOverlayMode mode,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("Capture selection is only available on Windows.");
        }

        await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunOverlayAsync(mode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private static Task<CaptureSelectionOverlayResult?> RunOverlayAsync(
        CaptureSelectionOverlayMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<CaptureSelectionOverlayResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var virtualBounds = SystemInformation.VirtualScreen;
                using var overlay = new CaptureSelectionOverlay(mode, virtualBounds);
                _ = overlay.Handle;
                using var registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (overlay.IsHandleCreated && !overlay.IsDisposed)
                        {
                            overlay.BeginInvoke(overlay.CancelSelection);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The form completed between the cancellation check
                        // and BeginInvoke. The normal close path wins.
                    }
                });
                Application.Run(overlay);
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                else
                {
                    completion.TrySetResult(overlay.Result);
                }
            }
            catch (OperationCanceledException cancelled)
            {
                completion.TrySetCanceled(cancelled.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(new ScreenshotCaptureException(
                    "The Windows capture selection overlay failed.",
                    error));
            }
        })
        {
            IsBackground = true,
            Name = "Crosio capture selection",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool Contains(PixelRect bounds, Point point) =>
        point.X >= bounds.Left
        && point.X < bounds.Right
        && point.Y >= bounds.Top
        && point.Y < bounds.Bottom;
}
