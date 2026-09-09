#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Crosio.Windows.LongCapture;

internal sealed record MouseWheelObservation(
    int ScreenX,
    int ScreenY,
    int HorizontalDelta,
    int VerticalDelta,
    bool IsInjected);

/// <summary>
/// A process-owned WH_MOUSE_LL monitor. It observes wheel input without moving
/// focus to Crosio and does not synthesize scrolling or require UI Automation.
/// </summary>
internal sealed class WindowsMouseWheelMonitor : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const uint WmQuit = 0x0012;
    private const uint LlMhfInjected = 0x00000001;

    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly Thread _thread;
    private readonly LowLevelMouseProc _callback;
    private nint _hook;
    private uint _threadId;
    private Exception? _startupError;
    private int _disposed;

    internal WindowsMouseWheelMonitor()
    {
        _callback = HookCallback;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Crosio long-capture wheel monitor",
        };
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
        {
            _ready.Dispose();
            throw new InvalidOperationException("The Windows wheel monitor could not start.", _startupError);
        }
    }

    internal event EventHandler<MouseWheelObservation>? WheelObserved;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_threadId != 0)
        {
            _ = PostThreadMessage(_threadId, WmQuit, nint.Zero, nint.Zero);
            if (Thread.CurrentThread != _thread)
            {
                _ = _thread.Join(TimeSpan.FromSeconds(3));
            }
        }

        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
            if (_hook == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _ready.Set();
            while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }
        }
        catch (Exception error)
        {
            _startupError = error;
            _ready.Set();
        }
        finally
        {
            if (_hook != nint.Zero)
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = nint.Zero;
            }
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (wParam == WmMouseWheel || wParam == WmMouseHWheel))
        {
            var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            var delta = unchecked((short)((data.MouseData >> 16) & 0xffff));
            var observation = new MouseWheelObservation(
                data.Point.X,
                data.Point.Y,
                wParam == WmMouseHWheel ? delta : 0,
                wParam == WmMouseWheel ? delta : 0,
                (data.Flags & LlMhfInjected) != 0);
            try
            {
                WheelObserved?.Invoke(this, observation);
            }
            catch
            {
                // A subscriber must never break the system hook chain.
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        internal readonly int X;
        internal readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MsllHookStruct
    {
        internal readonly NativePoint Point;
        internal readonly uint MouseData;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal NativePoint Point;
        internal uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint wParam,
        nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in NativeMessage message);
}
#endif
