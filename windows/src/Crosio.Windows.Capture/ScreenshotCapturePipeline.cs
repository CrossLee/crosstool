using Crosio.Windows.Capture.Clipboard;

namespace Crosio.Windows.Capture;

public sealed record ScreenshotCaptureDelivery(
    CapturedImage Image,
    bool WasCopiedToClipboard,
    Exception? ClipboardError);

/// <summary>
/// Enforces Crosio's hand-off order: materialize the final source PNG, attempt
/// to copy it immediately, and only then return it to the editor. A clipboard
/// failure is reported separately and never discards the captured image.
/// </summary>
public sealed class ScreenshotCapturePipeline
{
    private readonly IScreenshotCaptureService _captureService;
    private readonly IImageClipboard _clipboard;

    public ScreenshotCapturePipeline(
        IScreenshotCaptureService captureService,
        IImageClipboard clipboard)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    public async Task<ScreenshotCaptureDelivery> CaptureAndCopyAsync(
        ScreenshotCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        var image = await _captureService
            .CaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await DeliverAndCopyAsync(image, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the same clipboard-first hand-off to an image produced by a
    /// compositor or stitcher, such as a long screenshot. The final image is
    /// already eager at this boundary, so no intermediate frame can be copied.
    /// </summary>
    public async Task<ScreenshotCaptureDelivery> DeliverAndCopyAsync(
        CapturedImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            await _clipboard
                .WritePngAsync(image, cancellationToken)
                .ConfigureAwait(false);
            return new ScreenshotCaptureDelivery(image, true, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new ScreenshotCaptureDelivery(image, false, error);
        }
    }
}
