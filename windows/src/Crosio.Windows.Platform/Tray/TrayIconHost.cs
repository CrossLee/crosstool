using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Crosio.Windows.Platform.Tray;

/// <summary>
/// Creates a message-only window and owns Crosio's notification-area icon.
/// This lets a background/startup activation keep the app available without
/// constructing or activating its WinUI main window.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class TrayIconHost : IDisposable
{
    public const uint CallbackMessage = 0x0400 + 0x31;

    private const uint WmNcCreate = 0x0081;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint ErrorClassAlreadyExists = 1410;
    private const int GwlpUserData = -21;
    private const uint OpenMenuCommand = 1;
    private const uint ExitMenuCommand = 2;
    private const string WindowClassName = "Crosio.Tray.MessageWindow.v1";

    private static readonly object ClassRegistrationLock = new();
    private static readonly WindowProcedureDelegate WindowProcedure = WindowProcedureEntry;
    private static bool s_classRegistered;

    private readonly uint _ownerThreadId;
    private readonly uint _taskbarCreatedMessage;
    private GCHandle _selfHandle;
    private IntPtr _windowHandle;
    private TrayIconRegistration? _registration;
    private bool _disposed;

    public TrayIconHost(IntPtr iconHandle, Guid iconGuid, string tooltip = "一爪")
    {
        if (iconHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A valid HICON is required.", nameof(iconHandle));
        }

        _ownerThreadId = GetCurrentThreadId();
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        EnsureWindowClassRegistered();
        _selfHandle = GCHandle.Alloc(this);
        try
        {
            _windowHandle = CreateWindowEx(
                0,
                WindowClassName,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                new IntPtr(-3), // HWND_MESSAGE
                IntPtr.Zero,
                GetModuleHandle(null),
                GCHandle.ToIntPtr(_selfHandle));
            if (_windowHandle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not create the 一爪 tray message window.");
            }

            _registration = new TrayIconRegistration(
                _windowHandle,
                iconHandle,
                CallbackMessage,
                iconGuid,
                tooltip);
        }
        catch
        {
            if (_windowHandle != IntPtr.Zero)
            {
                _ = DestroyWindow(_windowHandle);
                _windowHandle = IntPtr.Zero;
            }

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }

            throw;
        }
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    public Exception? LastCallbackError { get; private set; }

    public bool IsStarted => _registration?.IsAdded == true;

    public void Start()
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registration!.Add();
    }

    public void UpdateTooltip(string tooltip)
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registration!.UpdateTooltip(tooltip);
    }

    public void ShowInformation(string title, string message)
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registration!.ShowInformation(title, message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EnsureOwnerThread();
        _registration?.Dispose();
        _registration = null;

        if (_windowHandle != IntPtr.Zero)
        {
            _ = SetWindowLongPtr(_windowHandle, GwlpUserData, IntPtr.Zero);
            _ = DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _disposed = true;
    }

    private void EnsureOwnerThread()
    {
        if (GetCurrentThreadId() != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "The tray host must be used and disposed on the thread that created it.");
        }
    }

    private void HandleWindowMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == _taskbarCreatedMessage)
            {
                if (_registration?.IsAdded == true)
                {
                    _registration.ReAddAfterTaskbarCreated();
                }
                return;
            }

            if (message != CallbackMessage)
            {
                return;
            }

            var notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            switch (notification)
            {
                case NinSelect:
                case NinKeySelect:
                case WmLeftButtonDoubleClick:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WmContextMenu:
                    ShowContextMenu(wParam);
                    break;
            }
        }
        catch (Exception exception)
        {
            // Exceptions must never cross a native WndProc boundary.
            LastCallbackError = exception;
        }
    }

    private void ShowContextMenu(IntPtr packedCoordinates)
    {
        var x = unchecked((int)(short)(packedCoordinates.ToInt64() & 0xFFFF));
        var y = unchecked((int)(short)((packedCoordinates.ToInt64() >> 16) & 0xFFFF));
        if (x == -1 && y == -1)
        {
            if (!GetCursorPosition(out var cursor))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            x = cursor.X;
            y = cursor.Y;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!AppendMenu(menu, MfString, OpenMenuCommand, "打开一爪") ||
                !AppendMenu(menu, MfSeparator, 0, null) ||
                !AppendMenu(menu, MfString, ExitMenuCommand, "退出"))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _ = SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCommand,
                x,
                y,
                _windowHandle,
                IntPtr.Zero);
            switch (command)
            {
                case OpenMenuCommand:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case ExitMenuCommand:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private static void EnsureWindowClassRegistered()
    {
        lock (ClassRegistrationLock)
        {
            if (s_classRegistered)
            {
                return;
            }

            var module = GetModuleHandle(null);
            var windowClass = new WindowClass
            {
                Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                Instance = module,
                ClassName = WindowClassName,
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorClassAlreadyExists)
                {
                    throw new Win32Exception(
                        error,
                        "Windows could not register the 一爪 tray message window.");
                }
            }

            s_classRegistered = true;
        }
    }

    private static IntPtr WindowProcedureEntry(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (message == WmNcCreate)
        {
            var create = Marshal.PtrToStructure<CreateStructure>(lParam);
            _ = SetWindowLongPtr(windowHandle, GwlpUserData, create.CreationParameter);
        }

        var value = GetWindowLongPtr(windowHandle, GwlpUserData);
        if (value != IntPtr.Zero)
        {
            var handle = GCHandle.FromIntPtr(value);
            if (handle.Target is TrayIconHost host)
            {
                host.HandleWindowMessage(message, wParam, lParam);
            }
        }

        return DefWindowProc(windowHandle, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedureDelegate(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CreateStructure
    {
        public IntPtr CreationParameter;
        public IntPtr Instance;
        public IntPtr Menu;
        public IntPtr Parent;
        public int Height;
        public int Width;
        public int Y;
        public int X;
        public int Style;
        public IntPtr Name;
        public IntPtr Class;
        public uint ExtendedStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr creationParameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint item, string? label);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr windowHandle,
        IntPtr parameters);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPosition(out Point point);
}
