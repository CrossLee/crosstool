namespace Crosio.Windows.Capture.Editor;

public interface IScreenshotEditorShareSink
{
    Task ShareAsync(
        CapturedImage image,
        CancellationToken cancellationToken = default);
}
