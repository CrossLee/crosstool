using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Crosio.Windows.Core.Settings;

namespace Crosio.Windows.Hotkeys;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsGlobalHotkeyHost : IGlobalHotkeyHost, IHotkeyRegistrationBackend
{
    private const uint WmNcCreate = 0x0081;
    private const uint WmHotkey = 0x0312;
    private const int GwlpUserData = -21;
    private const uint ErrorClassAlreadyExists = 1410;
    private const string WindowClassName = "Crosio.GlobalHotkeys.MessageWindow.v1";

    private static readonly object ClassRegistrationLock = new();
    private static readonly WindowProcedureDelegate WindowProcedure = WindowProcedureEntry;
    private static bool s_classRegistered;

    private readonly uint _ownerThreadId;
    private readonly GlobalHotkeyRegistrationCoordinator _coordinator;
    private IReadOnlyDictionary<string, HotkeyBinding>? _bindingsBeforeRecording;
    private GCHandle _selfHandle;
    private IntPtr _windowHandle;
    private bool _disposed;

    public WindowsGlobalHotkeyHost()
    {
        _ownerThreadId = GetCurrentThreadId();
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
                new IntPtr(-3),
                IntPtr.Zero,
                GetModuleHandle(null),
                GCHandle.ToIntPtr(_selfHandle));
            if (_windowHandle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows 无法创建 Crosio 快捷键消息窗口");
            }

            _coordinator = new GlobalHotkeyRegistrationCoordinator(this);
        }
        catch
        {
            if (_windowHandle != IntPtr.Zero)
            {
                _ = DestroyWindow(_windowHandle);
            }
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
            throw;
        }
    }

    public event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;

    public IReadOnlyDictionary<string, HotkeyBinding> ActiveBindings => _coordinator.ActiveBindings;

    public GlobalHotkeyUpdateResult ReplaceBindings(IReadOnlyDictionary<string, HotkeyBinding> bindings)
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bindingsBeforeRecording is not null)
        {
            var suspendedUpdate = _coordinator.ReplaceBindings(bindings);
            if (suspendedUpdate.Succeeded)
            {
                _bindingsBeforeRecording = null;
            }
            return suspendedUpdate;
        }
        return _coordinator.ReplaceBindings(bindings);
    }

    public GlobalHotkeyUpdateResult SuspendForRecording()
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bindingsBeforeRecording is not null)
        {
            return GlobalHotkeyUpdateResult.Success(ActiveBindings);
        }

        var snapshot = ActiveBindings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var result = _coordinator.ReplaceBindings(
            new Dictionary<string, HotkeyBinding>(StringComparer.OrdinalIgnoreCase));
        if (result.Succeeded)
        {
            _bindingsBeforeRecording = snapshot;
        }
        return result;
    }

    public GlobalHotkeyUpdateResult ResumeAfterRecording()
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bindingsBeforeRecording is null)
        {
            return GlobalHotkeyUpdateResult.Success(ActiveBindings);
        }

        var result = _coordinator.ReplaceBindings(_bindingsBeforeRecording);
        if (result.Succeeded)
        {
            _bindingsBeforeRecording = null;
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EnsureOwnerThread();
        _coordinator.Dispose();
        _bindingsBeforeRecording = null;
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

    bool IHotkeyRegistrationBackend.TryRegister(int id, NativeHotkey hotkey, out int nativeErrorCode)
    {
        if (RegisterHotKey(_windowHandle, id, hotkey.Modifiers, hotkey.VirtualKey))
        {
            nativeErrorCode = 0;
            return true;
        }

        nativeErrorCode = Marshal.GetLastWin32Error();
        return false;
    }

    void IHotkeyRegistrationBackend.Unregister(int id) => _ = UnregisterHotKey(_windowHandle, id);

    private void HandleWindowMessage(uint message, IntPtr wParam)
    {
        if (message != WmHotkey)
        {
            return;
        }

        var route = _coordinator.RouteForRegistrationId(wParam.ToInt32());
        if (route is not null)
        {
            Pressed?.Invoke(this, new GlobalHotkeyPressedEventArgs(route));
        }
    }

    private void EnsureOwnerThread()
    {
        if (GetCurrentThreadId() != _ownerThreadId)
        {
            throw new InvalidOperationException("全局快捷键必须在创建它的 UI 线程上更新");
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
                    throw new Win32Exception(error, "Windows 无法注册 Crosio 快捷键窗口类");
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
            if (handle.Target is WindowsGlobalHotkeyHost host)
            {
                try
                {
                    host.HandleWindowMessage(message, wParam);
                }
                catch
                {
                    // Exceptions may not cross a native window procedure boundary.
                }
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

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
        IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    private static IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : SetWindowLong32(windowHandle, index, value);

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : GetWindowLong32(windowHandle, index);
}
