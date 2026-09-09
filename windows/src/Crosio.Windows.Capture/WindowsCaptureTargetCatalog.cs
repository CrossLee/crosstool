using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Crosio.Windows.Capture.Interop;

namespace Crosio.Windows.Capture;

/// <summary>
/// Enumerates the current top-level windows and displays. Every returned
/// rectangle uses virtual-desktop physical pixel coordinates.
/// </summary>
public sealed class WindowsCaptureTargetCatalog : ICaptureTargetCatalog
{
    public CaptureTargetSnapshot GetSnapshot(uint? excludedProcessId = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("一爪 requires Windows 10 version 2004 or newer.");
        }

        return new CaptureTargetSnapshot(
            EnumerateDisplays(),
            EnumerateWindows(excludedProcessId));
    }

    private static IReadOnlyList<CaptureDisplay> EnumerateDisplays()
    {
        return Screen.AllScreens
            .Select(screen => new CaptureDisplay(
                screen.DeviceName,
                ToPixelRect(screen.Bounds),
                ToPixelRect(screen.WorkingArea),
                screen.Primary))
            .ToArray();
    }

    private static IReadOnlyList<CaptureWindow> EnumerateWindows(uint? excludedProcessId)
    {
        var windows = new List<CaptureWindow>();
        var shellWindow = NativeMethods.GetShellWindow();
        NativeMethods.EnumWindowsCallback callback = (windowHandle, _) =>
        {
            if (windowHandle == shellWindow
                || !NativeMethods.IsWindowVisible(windowHandle)
                || NativeMethods.IsIconic(windowHandle)
                || IsToolWindow(windowHandle)
                || IsCloaked(windowHandle))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
            if (excludedProcessId == processId)
            {
                return true;
            }

            var titleLength = NativeMethods.GetWindowTextLengthW(windowHandle);
            if (titleLength <= 0)
            {
                return true;
            }

            var titleBuilder = new StringBuilder(titleLength + 1);
            if (NativeMethods.GetWindowTextW(
                    windowHandle,
                    titleBuilder,
                    titleBuilder.Capacity) <= 0)
            {
                return true;
            }

            var title = titleBuilder.ToString().Trim();
            if (title.Length == 0 || !TryGetWindowBounds(windowHandle, out var bounds))
            {
                return true;
            }

            windows.Add(new CaptureWindow(windowHandle, title, processId, bounds));
            return true;
        };

        if (!NativeMethods.EnumWindows(callback, nint.Zero))
        {
            throw new ScreenshotCaptureException("Windows could not enumerate capturable windows.");
        }

        GC.KeepAlive(callback);
        return windows;
    }

    internal static bool TryGetWindowBounds(nint windowHandle, out PixelRect bounds)
    {
        bounds = default;
        if (!NativeMethods.IsWindow(windowHandle) || NativeMethods.IsIconic(windowHandle))
        {
            return false;
        }

        NativeRect rectangle;
        var result = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DwmwaExtendedFrameBounds,
            out rectangle,
            Marshal.SizeOf<NativeRect>());
        if (result != 0 && !NativeMethods.GetWindowRect(windowHandle, out rectangle))
        {
            return false;
        }

        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return false;
        }

        bounds = new PixelRect(
            rectangle.Left,
            rectangle.Top,
            rectangle.Width,
            rectangle.Height);
        return true;
    }

    internal static PixelRect GetVirtualDesktopBounds()
    {
        return new PixelRect(
            NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));
    }

    private static bool IsToolWindow(nint windowHandle)
    {
        var extendedStyle = NativeMethods.GetWindowExtendedStyle(windowHandle).ToInt64();
        return (extendedStyle & NativeMethods.WsExToolWindow) != 0;
    }

    private static bool IsCloaked(nint windowHandle)
    {
        var result = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DwmwaCloaked,
            out int cloaked,
            sizeof(int));
        return result == 0 && cloaked != 0;
    }

    private static PixelRect ToPixelRect(Rectangle rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height);
}
