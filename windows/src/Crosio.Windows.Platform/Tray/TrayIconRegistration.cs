using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Crosio.Windows.Platform.Tray;

/// <summary>
/// Owns a Windows notification-area registration. The WinUI host supplies a
/// hidden/message window HWND and handles CallbackMessage in its WndProc.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class TrayIconRegistration : IDisposable
{
    public const uint NotifyIconVersion4 = 4;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifGuid = 0x00000020;
    private const uint NiifInfo = 0x00000001;

    private readonly IntPtr _windowHandle;
    private readonly IntPtr _iconHandle;
    private readonly uint _callbackMessage;
    private readonly Guid _iconGuid;
    private string _tooltip;
    private bool _isAdded;
    private bool _disposed;

    public TrayIconRegistration(
        IntPtr windowHandle,
        IntPtr iconHandle,
        uint callbackMessage,
        Guid iconGuid,
        string tooltip = "Crosio")
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A message-window HWND is required.", nameof(windowHandle));
        }

        if (iconHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A valid HICON is required.", nameof(iconHandle));
        }

        if (callbackMessage < 0x0400)
        {
            throw new ArgumentOutOfRangeException(
                nameof(callbackMessage),
                "Use an application-defined message at or above WM_USER.");
        }

        _windowHandle = windowHandle;
        _iconHandle = iconHandle;
        _callbackMessage = callbackMessage;
        _iconGuid = iconGuid == Guid.Empty ? throw new ArgumentException("A stable icon GUID is required.", nameof(iconGuid)) : iconGuid;
        _tooltip = NormalizeTooltip(tooltip);
    }

    public bool IsAdded => _isAdded;

    public void Add()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isAdded)
        {
            return;
        }

        var data = CreateData(NifMessage | NifIcon | NifTip | NifGuid);
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not add the Crosio tray icon.");
        }

        data.VersionOrTimeout = NotifyIconVersion4;
        if (!ShellNotifyIcon(NimSetVersion, ref data))
        {
            _ = ShellNotifyIcon(NimDelete, ref data);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not set the Crosio tray icon version.");
        }

        _isAdded = true;
    }

    public void ReAddAfterTaskbarCreated()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _isAdded = false;
        Add();
    }

    public void UpdateTooltip(string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tooltip = NormalizeTooltip(tooltip);
        if (!_isAdded)
        {
            return;
        }

        var data = CreateData(NifTip | NifGuid);
        if (!ShellNotifyIcon(NimModify, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not update the Crosio tray tooltip.");
        }
    }

    public void ShowInformation(string title, string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isAdded)
        {
            throw new InvalidOperationException("Add the tray icon before showing a notification.");
        }

        var data = CreateData(NifInfo | NifGuid);
        data.InfoTitle = Normalize(title, 63);
        data.Info = Normalize(message, 255);
        data.InfoFlags = NiifInfo;
        if (!ShellNotifyIcon(NimModify, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not show the Crosio notification.");
        }
    }

    public void Remove()
    {
        if (!_isAdded)
        {
            return;
        }

        var data = CreateData(NifGuid);
        _ = ShellNotifyIcon(NimDelete, ref data);
        _isAdded = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Remove();
        _disposed = true;
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
        WindowHandle = _windowHandle,
        Identifier = 1,
        Flags = flags,
        CallbackMessage = _callbackMessage,
        IconHandle = _iconHandle,
        Tip = _tooltip,
        IconGuid = _iconGuid,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private static string NormalizeTooltip(string value) => Normalize(value, 127);

    private static string Normalize(string? value, int maximumCharacters)
    {
        var normalized = (value ?? string.Empty).Replace("\0", string.Empty);
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters];
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Identifier;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid IconGuid;
        public IntPtr BalloonIconHandle;
    }
}
