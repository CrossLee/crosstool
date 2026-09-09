using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Crosio.Windows.Capture.Interop;

namespace Crosio.Windows.Capture;

/// <summary>
/// First Windows capture backend. It captures the composited desktop in the
/// exact requested bounds, preserving negative-coordinate monitor layouts and
/// visible window shadows. The public target boundary is intentionally ready
/// for a Windows.Graphics.Capture backend without changing App code.
/// </summary>
public sealed class WindowsScreenshotCaptureService : IScreenshotCaptureService
{
    public const long DefaultMaximumPixelCount = 100_000_000;

    public ScreenshotCaptureCapabilities Capabilities { get; } = new(
        SupportsVirtualDesktop: true,
        SupportsDisplay: true,
        SupportsWindow: true,
        SupportsRegion: true,
        SupportsCursor: true,
        CapturesOccludedWindowContents: false,
        CapturesMinimizedWindowContents: false);

    public Task<CapturedImage> CaptureAsync(
        ScreenshotCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureSupportedPlatform();
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(
            () => CaptureCore(request, cancellationToken),
            cancellationToken);
    }

    private static CapturedImage CaptureCore(
        ScreenshotCaptureRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = ResolveBounds(request.Target);
        if (bounds.PixelCount > request.MaximumPixelCount)
        {
            throw new ScreenshotCaptureException(
                $"The requested screenshot contains {bounds.PixelCount:N0} pixels, "
                + $"which exceeds the {request.MaximumPixelCount:N0}-pixel safety limit.");
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
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CopyFromScreen(
                    bounds.Left,
                    bounds.Top,
                    0,
                    0,
                    new Size(bounds.Width, bounds.Height),
                    CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

                if (request.IncludeCursor)
                {
                    DrawCursor(graphics, bounds);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            return new CapturedImage(output.GetBuffer().AsSpan(0, checked((int)output.Length)), bounds.Width, bounds.Height);
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
                "Windows could not capture the selected screen content.",
                error);
        }
    }

    private static PixelRect ResolveBounds(ScreenshotCaptureTarget target)
    {
        return target switch
        {
            VirtualDesktopCaptureTarget => WindowsCaptureTargetCatalog.GetVirtualDesktopBounds(),
            DisplayCaptureTarget display => display.Bounds,
            RegionCaptureTarget region => region.Bounds,
            WindowCaptureTarget window
                when WindowsCaptureTargetCatalog.TryGetWindowBounds(window.WindowHandle, out var bounds)
                => bounds,
            WindowCaptureTarget => throw new ScreenshotCaptureException(
                "The selected window is no longer available or is minimized."),
            _ => throw new NotSupportedException($"Unsupported capture target: {target.GetType().Name}."),
        };
    }

    private static void DrawCursor(Graphics graphics, PixelRect captureBounds)
    {
        var cursorInfo = new CursorInfo
        {
            Size = Marshal.SizeOf<CursorInfo>(),
        };
        if (!NativeMethods.GetCursorInfo(ref cursorInfo)
            || (cursorInfo.Flags & NativeMethods.CursorShowing) == 0
            || !NativeMethods.GetIconInfo(cursorInfo.CursorHandle, out var iconInfo))
        {
            return;
        }

        try
        {
            var x = checked(cursorInfo.ScreenPosition.X - (int)iconInfo.HotspotX - captureBounds.Left);
            var y = checked(cursorInfo.ScreenPosition.Y - (int)iconInfo.HotspotY - captureBounds.Top);
            var deviceContext = graphics.GetHdc();
            try
            {
                _ = NativeMethods.DrawIconEx(
                    deviceContext,
                    x,
                    y,
                    cursorInfo.CursorHandle,
                    0,
                    0,
                    0,
                    nint.Zero,
                    NativeMethods.DiNormal);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }
        finally
        {
            if (iconInfo.ColorBitmap != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(iconInfo.ColorBitmap);
            }

            if (iconInfo.MaskBitmap != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(iconInfo.MaskBitmap);
            }
        }
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("一爪 requires Windows 10 version 2004 or newer.");
        }
    }
}
