using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Crosio.Windows.Capture.Interop;

namespace Crosio.Windows.Capture;

public interface ISelectedWindowCaptureService
{
    Task<CapturedImage> CaptureAsync(
        CaptureWindow selectedWindow,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Captures a specifically selected HWND into its own device context. It does
/// not fall back to copying desktop pixels, because that could pull an
/// unselected occluding window into a multi-window composite.
/// </summary>
public sealed class WindowsSelectedWindowCaptureService : ISelectedWindowCaptureService
{
    public Task<CapturedImage> CaptureAsync(
        CaptureWindow selectedWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedWindow);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("窗口截图仅支持 Windows 10 2004 或更高版本。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => CaptureCore(selectedWindow, cancellationToken),
            cancellationToken);
    }

    private static CapturedImage CaptureCore(
        CaptureWindow selectedWindow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.IsWindow(selectedWindow.WindowHandle)
            || !NativeMethods.IsWindowVisible(selectedWindow.WindowHandle)
            || NativeMethods.IsIconic(selectedWindow.WindowHandle))
        {
            throw new ScreenshotCaptureException(
                $"所选窗口“{selectedWindow.Title}”已关闭、隐藏或最小化。");
        }

        var bounds = selectedWindow.Bounds;
        if (bounds.PixelCount > WindowsScreenshotCaptureService.DefaultMaximumPixelCount)
        {
            throw new ScreenshotCaptureException(
                $"所选窗口“{selectedWindow.Title}”超过截图安全限制。");
        }

        try
        {
            using var bitmap = new Bitmap(
                bounds.Width,
                bounds.Height,
                PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                var deviceContext = graphics.GetHdc();
                try
                {
                    if (!NativeMethods.PrintWindow(
                            selectedWindow.WindowHandle,
                            deviceContext,
                            NativeMethods.PrintWindowRenderFullContent))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            $"Windows 无法读取所选窗口“{selectedWindow.Title}”的内容。");
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return new CapturedImage(
                output.GetBuffer().AsSpan(0, checked((int)output.Length)),
                bitmap.Width,
                bitmap.Height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (
            error is ExternalException
            or Win32Exception
            or ArgumentException
            or OutOfMemoryException)
        {
            throw new ScreenshotCaptureException(
                $"无法截取所选窗口“{selectedWindow.Title}”；未使用全屏截图替代。",
                error);
        }
    }
}
