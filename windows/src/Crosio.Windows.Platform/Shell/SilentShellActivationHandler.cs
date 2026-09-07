using Crosio.Windows.Platform.Images;

namespace Crosio.Windows.Platform.Shell;

public sealed record SilentShellActivationResult(
    bool ShouldShowMainWindow,
    ImageFileActivationResult? ImageCompressionResult,
    string? ErrorMessage);

/// <summary>
/// Executes shell/startup activations without creating or activating a window.
/// The app calls this before constructing its main window.
/// </summary>
public sealed class SilentShellActivationHandler
{
    private readonly IClipboardPathService _clipboardPathService;
    private readonly ImageFileActivationHandler _imageFileActivationHandler;

    public SilentShellActivationHandler(
        IClipboardPathService clipboardPathService,
        ImageFileActivationHandler imageFileActivationHandler)
    {
        _clipboardPathService = clipboardPathService;
        _imageFileActivationHandler = imageFileActivationHandler;
    }

    public async Task<SilentShellActivationResult> HandleAsync(
        FileActivationRequest request,
        ImageCompressionSettings compressionSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (request.Operation)
        {
            case FileActivationOperation.ShowMainWindow:
                return new SilentShellActivationResult(true, null, null);
            case FileActivationOperation.BackgroundOnly:
                return new SilentShellActivationResult(false, null, null);
            case FileActivationOperation.CopyPaths:
                try
                {
                    _clipboardPathService.CopyPaths(request.Paths);
                    return new SilentShellActivationResult(false, null, null);
                }
                catch (Exception exception)
                {
                    return new SilentShellActivationResult(false, null, exception.Message);
                }
            case FileActivationOperation.CompressImages:
                var compressionResult = await _imageFileActivationHandler.HandleAsync(
                    request.Paths,
                    compressionSettings,
                    cancellationToken);
                return new SilentShellActivationResult(false, compressionResult, null);
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }
}
