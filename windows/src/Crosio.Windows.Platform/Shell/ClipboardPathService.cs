using System.ComponentModel;
using Crosio.Windows.Platform.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Crosio.Windows.Platform.Shell;

public interface IClipboardPathService
{
    void CopyPaths(IReadOnlyList<string> paths);
}

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ClipboardPathService : IClipboardPathService
{
    private const uint GmemMoveable = 0x0002;
    private const uint CfUnicodeText = 13;
    private const int ClipboardRetryCount = 8;
    private const int ClipboardRetryDelayMilliseconds = 15;

    public void CopyPaths(IReadOnlyList<string> paths)
    {
        var text = ClipboardPathFormatter.Format(paths);
        var characters = (text + '\0').ToCharArray();
        var memory = GlobalAlloc(
            GmemMoveable,
            checked((nuint)(characters.Length * sizeof(char))));
        if (memory == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var ownerWindow = IntPtr.Zero;
        var clipboardOpened = false;
        try
        {
            var destination = GlobalLock(memory);
            if (destination == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                Marshal.Copy(characters, 0, destination, characters.Length);
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }

            // A message-only window gives EmptyClipboard a real owner without
            // opening or activating any application UI.
            ownerWindow = CreateWindowExW(
                0, "STATIC", "Crosio clipboard owner", 0,
                0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (ownerWindow == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var clipboardError = 0;
            for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
            {
                if (OpenClipboard(ownerWindow))
                {
                    clipboardOpened = true;
                    break;
                }

                clipboardError = Marshal.GetLastWin32Error();
                Thread.Sleep(ClipboardRetryDelayMilliseconds);
            }

            if (!clipboardOpened)
            {
                throw new Win32Exception(
                    clipboardError,
                    "The Windows clipboard is busy. Please try Copy Path again.");
            }

            if (!EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            // Ownership transfers to the operating system after SetClipboardData.
            memory = IntPtr.Zero;
        }
        finally
        {
            if (clipboardOpened)
            {
                _ = CloseClipboard();
            }

            if (ownerWindow != IntPtr.Zero)
            {
                _ = DestroyWindow(ownerWindow);
            }

            if (memory != IntPtr.Zero)
            {
                _ = GlobalFree(memory);
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentWindow,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr ownerWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}

public static class ClipboardPathFormatter
{
    public static string Format(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one selected path is required.", nameof(paths));
        }

        var validated = new string[paths.Count];
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            if (string.IsNullOrWhiteSpace(path) ||
                path.IndexOf('\0') >= 0 ||
                path.IndexOfAny(['\r', '\n']) >= 0 ||
                !WindowsPathPolicy.IsFullyQualified(path))
            {
                throw new ArgumentException(
                    "Every selected item must have a fully qualified path without line breaks.",
                    nameof(paths));
            }

            // Do not call GetFullPath or resolve reparse points: copy the path
            // selected by the user, not a normalized path to another target.
            validated[index] = path;
        }

        return string.Join("\r\n", validated);
    }
}
