#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Crosio.Windows.Media;

/// <summary>
/// Samples the composited desktop through GetPixel at a physical virtual-desktop
/// coordinate. The App must hide its own picker/preview windows before sampling
/// so Crosio does not read its own overlay.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed partial class WindowsScreenColorSampler : IScreenColorSampler
{
    private const uint InvalidColor = 0xFFFFFFFF;

    public ValueTask<ScreenColorSample> SampleAsync(
        PixelPoint position,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var desktopDeviceContext = NativeMethods.GetDC(nint.Zero);
        if (desktopDeviceContext == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "一爪 could not acquire the desktop device context for color sampling.");
        }

        try
        {
            var colorReference = NativeMethods.GetPixel(desktopDeviceContext, position.X, position.Y);
            if (colorReference == InvalidColor)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "一爪 could not read the selected screen pixel.");
            }

            // COLORREF stores 0x00BBGGRR.
            var color = new SampledColor(
                (byte)(colorReference & 0xFF),
                (byte)((colorReference >> 8) & 0xFF),
                (byte)((colorReference >> 16) & 0xFF));
            return ValueTask.FromResult(new ScreenColorSample(position, color, DateTimeOffset.UtcNow));
        }
        finally
        {
            _ = NativeMethods.ReleaseDC(nint.Zero, desktopDeviceContext);
        }
    }

    public ValueTask<ScreenColorSample> SampleCursorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.GetCursorPos(out var point))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "一爪 could not read the current pointer position.");
        }

        return SampleAsync(new PixelPoint(point.X, point.Y), cancellationToken);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint GetDC(nint windowHandle);

        [LibraryImport("user32.dll")]
        internal static partial int ReleaseDC(nint windowHandle, nint deviceContext);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial uint GetPixel(nint deviceContext, int x, int y);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetCursorPos(out NativePoint point);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
#endif
