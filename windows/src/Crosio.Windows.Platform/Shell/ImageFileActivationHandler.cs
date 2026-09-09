using Crosio.Windows.Platform.Images;
using Crosio.Windows.Platform.IO;

namespace Crosio.Windows.Platform.Shell;

public sealed record ImageFileActivationItemResult(
    string SourcePath,
    ImageCompressionOutcome? Outcome,
    string? ErrorMessage)
{
    public bool Succeeded => Outcome is not null;
}

public sealed record ImageFileActivationResult(
    IReadOnlyList<ImageFileActivationItemResult> Items,
    IReadOnlyList<string> RevealedOutputPaths)
{
    // File activation is an operation, not a request to open Crosio's main UI.
    public bool ShouldShowMainWindow => false;
}

public sealed class ImageFileActivationHandler
{
    private readonly IImageCompressionService _compressionService;
    private readonly IExplorerRevealService _explorerRevealService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public ImageFileActivationHandler(
        IImageCompressionService compressionService,
        IExplorerRevealService explorerRevealService)
    {
        _compressionService = compressionService ?? throw new ArgumentNullException(nameof(compressionService));
        _explorerRevealService = explorerRevealService ?? throw new ArgumentNullException(nameof(explorerRevealService));
    }

    public async Task<ImageFileActivationResult> HandleAsync(
        IEnumerable<string> sourcePaths,
        ImageCompressionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(settings);

        var paths = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<ImageFileActivationItemResult>(paths.Length);
            var outputPaths = new List<string>();

            // Keep the entire batch, including its single Explorer reveal,
            // behind the app-wide handler gate. Individual images remain
            // sequential so only one decoded pixel buffer can exist at a time.
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!WindowsPathPolicy.IsFullyQualified(path) ||
                    !ImageCompressionPathPolicy.IsSupportedImagePath(path))
                {
                    results.Add(new ImageFileActivationItemResult(
                        path,
                        null,
                        "一爪 only accepts fully qualified JPEG, PNG, HEIC, HEIF, or TIFF image paths."));
                    continue;
                }

                try
                {
                    var outcome = await _compressionService.CompressAsync(
                        path,
                        settings,
                        cancellationToken).ConfigureAwait(false);
                    results.Add(new ImageFileActivationItemResult(path, outcome, null));
                    if (outcome.Result is not null)
                    {
                        outputPaths.Add(outcome.Result.OutputPath);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    results.Add(new ImageFileActivationItemResult(path, null, exception.Message));
                }
            }

            var uniqueOutputs = outputPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            if (uniqueOutputs.Length > 0)
            {
                _explorerRevealService.RevealFiles(uniqueOutputs);
            }
            cancellationToken.ThrowIfCancellationRequested();

            return new ImageFileActivationResult(results, uniqueOutputs);
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
