namespace Crosio.Windows.Capture.Composition;

/// <summary>
/// Keeps advanced screenshot ordering explicit and testable: acquire all
/// source pixels, materialize exactly one final composite, then deliver only
/// that final image to the clipboard pipeline.
/// </summary>
public sealed class AdvancedScreenshotCaptureCoordinator
{
    private readonly IScreenshotCaptureService _screenCapture;
    private readonly ISelectedWindowCaptureService _windowCapture;
    private readonly IScreenshotComposer _composer;
    private readonly ScreenshotCapturePipeline _delivery;

    public AdvancedScreenshotCaptureCoordinator(
        IScreenshotCaptureService screenCapture,
        ISelectedWindowCaptureService windowCapture,
        IScreenshotComposer composer,
        ScreenshotCapturePipeline delivery)
    {
        _screenCapture = screenCapture ?? throw new ArgumentNullException(nameof(screenCapture));
        _windowCapture = windowCapture ?? throw new ArgumentNullException(nameof(windowCapture));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    public async Task<ScreenshotCaptureDelivery> CaptureFramedAndCopyAsync(
        DisplayCaptureTarget display,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(display);
        var source = await _screenCapture.CaptureAsync(
            new ScreenshotCaptureRequest(display),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var finalImage = _composer.AddCrosioDeviceFrame(source);
        return await _delivery.DeliverAndCopyAsync(finalImage, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ScreenshotCaptureDelivery?> CaptureSelectedWindowsAndCopyAsync(
        IReadOnlyList<CaptureWindow>? selectedWindows,
        CancellationToken cancellationToken = default)
    {
        if (selectedWindows is null || selectedWindows.Count == 0)
        {
            return null;
        }

        var items = new List<WindowCompositeItem>(selectedWindows.Count);
        foreach (var selectedWindow in selectedWindows)
        {
            var image = await _windowCapture
                .CaptureAsync(selectedWindow, cancellationToken)
                .ConfigureAwait(false);
            items.Add(new WindowCompositeItem(
                image,
                selectedWindow.Bounds,
                selectedWindow.Title));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finalImage = _composer.ComposeSelectedWindows(items);
        return await _delivery.DeliverAndCopyAsync(finalImage, cancellationToken)
            .ConfigureAwait(false);
    }
}
