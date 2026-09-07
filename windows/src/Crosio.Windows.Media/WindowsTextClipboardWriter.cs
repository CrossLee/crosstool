#if WINDOWS
using System.Runtime.Versioning;
using Windows.ApplicationModel.DataTransfer;

namespace Crosio.Windows.Media;

[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsTextClipboardWriter : ITextClipboardWriter
{
    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };
                package.SetText(text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                completion.SetResult();
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Crosio color clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
#endif
