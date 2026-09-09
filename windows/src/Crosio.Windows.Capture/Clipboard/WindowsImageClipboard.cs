using System.Drawing;
using System.Runtime.InteropServices;

namespace Crosio.Windows.Capture.Clipboard;

/// <summary>
/// Publishes both a normal Windows bitmap and the eager PNG payload. The
/// operation runs on its own STA thread so capture completion never depends on
/// whether the caller is a WinUI dispatcher thread.
/// </summary>
public sealed class WindowsImageClipboard : IImageClipboard
{
    private const int RetryCount = 5;
    private const int RetryDelayMilliseconds = 40;

    public Task WritePngAsync(
        CapturedImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("The image clipboard is only available on Windows.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var eagerPng = image.CopyPngBytes();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = new MemoryStream(eagerPng, writable: false);
                using var decoded = Image.FromStream(
                    source,
                    useEmbeddedColorManagement: true,
                    validateImageData: true);
                using var bitmap = new Bitmap(decoded);
                using var pngPayload = new MemoryStream(eagerPng, writable: false);

                var dataObject = new DataObject();
                dataObject.SetImage(bitmap);
                dataObject.SetData("PNG", autoConvert: false, pngPayload);
                System.Windows.Forms.Clipboard.SetDataObject(
                    dataObject,
                    copy: true,
                    RetryCount,
                    RetryDelayMilliseconds);
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException cancelled)
            {
                completion.TrySetCanceled(cancelled.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(new ImageClipboardException(
                    "Windows rejected the screenshot clipboard payload.",
                    error));
            }
        })
        {
            IsBackground = true,
            Name = "Crosio image clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
