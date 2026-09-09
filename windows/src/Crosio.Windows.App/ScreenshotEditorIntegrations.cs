using Crosio.Windows.Capture;
using Crosio.Windows.Capture.Editor;
using Crosio.Windows.Intelligence.Ocr;
using Crosio.Windows.Sharing;

namespace Crosio.Windows.App;

internal sealed class ScreenshotEditorOcrService(IImageOcrService ocr) : IScreenshotEditorOcrService
{
    private readonly IImageOcrService _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));

    public async Task<string> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var document = await _ocr.RecognizeEncodedAsync(
            image.PngBytes,
            ["zh-Hans", "en-US"],
            cancellationToken).ConfigureAwait(false);
        return document.Text;
    }
}

internal sealed class ScreenshotEditorShareSink : IScreenshotEditorShareSink
{
    private readonly string _screenshotsDirectory;
    private readonly SharedContentStore _sharedContent;

    public ScreenshotEditorShareSink(
        string screenshotsDirectory,
        SharedContentStore sharedContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshotsDirectory);
        _screenshotsDirectory = Path.GetFullPath(screenshotsDirectory);
        _sharedContent = sharedContent ?? throw new ArgumentNullException(nameof(sharedContent));
    }

    public async Task ShareAsync(
        CapturedImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PngBytes.Length > SharedContentStore.MaximumUploadBytes)
        {
            throw new SharedContentException("截图超过当前版本的 256 MB 共享限制");
        }

        Directory.CreateDirectory(_screenshotsDirectory);
        var fileName = $"一爪-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
        var destination = UniqueDestination(fileName);
        var temporary = Path.Combine(
            _screenshotsDirectory,
            $".{Guid.NewGuid():N}.screenshot-part");

        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                image.CopyPngBytes(),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination);
            _sharedContent.AddSharedFile(destination);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A best-effort cleanup failure must not hide the share result.
            }
        }
    }

    private string UniqueDestination(string fileName)
    {
        var candidate = Path.Combine(_screenshotsDirectory, fileName);
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(
                _screenshotsDirectory,
                $"{Path.GetFileNameWithoutExtension(fileName)}-{suffix}.png");
        }

        return candidate;
    }
}
