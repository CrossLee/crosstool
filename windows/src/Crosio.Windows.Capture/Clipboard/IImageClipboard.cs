namespace Crosio.Windows.Capture.Clipboard;

public interface IImageClipboard
{
    Task WritePngAsync(
        CapturedImage image,
        CancellationToken cancellationToken = default);
}
