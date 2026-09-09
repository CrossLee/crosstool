using System.Runtime.InteropServices;
using System.Text;

namespace Crosio.Windows.Capture.Interop;

internal static class NativeMethods
{
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x00000080L;
    internal const int DwmwaExtendedFrameBounds = 9;
    internal const int DwmwaCloaked = 14;

    internal const uint CursorShowing = 0x00000001;
    internal const uint DiNormal = 0x0003;
    internal const uint PrintWindowRenderFullContent = 0x00000002;

    internal const uint WdaExcludeFromCapture = 0x00000011;
    internal const int VkLeftWindows = 0x5B;
    internal const int VkRightWindows = 0x5C;

    internal delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(
        EnumWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(
        nint windowHandle,
        StringBuilder value,
        int maximumCharacterCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static extern int GetWindowLong32(nint windowHandle, int index);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint windowHandle,
        int attribute,
        out NativeRect value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint windowHandle,
        int attribute,
        out int value,
        int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetIconInfo(nint iconHandle, out IconInfo iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DrawIconEx(
        nint deviceContext,
        int x,
        int y,
        nint iconHandle,
        int width,
        int height,
        uint animationStep,
        nint flickerFreeBrush,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(
        nint windowHandle,
        nint deviceContext,
        uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    internal static nint GetWindowExtendedStyle(nint windowHandle) =>
        nint.Size == 8
            ? GetWindowLongPtr64(windowHandle, GwlExStyle)
            : new nint(GetWindowLong32(windowHandle, GwlExStyle));
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;

    internal int Width => checked(Right - Left);

    internal int Height => checked(Bottom - Top);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CursorInfo
{
    internal int Size;
    internal uint Flags;
    internal nint CursorHandle;
    internal NativePoint ScreenPosition;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IconInfo
{
    [MarshalAs(UnmanagedType.Bool)]
    internal bool IsIcon;

    internal uint HotspotX;
    internal uint HotspotY;
    internal nint MaskBitmap;
    internal nint ColorBitmap;
}
