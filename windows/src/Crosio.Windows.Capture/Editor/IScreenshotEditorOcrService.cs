namespace Crosio.Windows.Capture.Editor;

/// <summary>
/// Optional OCR boundary used by the editor. Implementations receive the
/// original screenshot after the editor opens; recognition itself never writes
/// to the clipboard, so the eagerly copied image remains intact until the user
/// explicitly presses “复制文字”.
/// </summary>
public interface IScreenshotEditorOcrService
{
    Task<string> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken = default);
}
