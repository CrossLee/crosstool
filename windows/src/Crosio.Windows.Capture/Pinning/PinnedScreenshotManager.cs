using System.Drawing;
using System.Runtime.Versioning;
using Crosio.Windows.Capture.Interop;

namespace Crosio.Windows.Capture.Pinning;

public sealed record PinnedScreenshotOptions(
    Point? Anchor = null,
    bool SelectForKeyboard = true);

/// <summary>
/// Owns independent, always-on-top image windows on a dedicated STA message
/// loop. Multiple pins never retain screenshot draft files.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class PinnedScreenshotManager : IPinnedScreenshotService, IDisposable
{
    public const long DefaultMemoryBudgetBytes = 128L * 1024 * 1024;
    public const long DefaultMaximumSnapshotPixelCount = 3_000_000;

    private readonly long _memoryBudgetBytes;
    private readonly long _maximumSnapshotPixelCount;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly Thread _uiThread;
    private readonly Dictionary<Guid, PinEntry> _pins = [];
    private SynchronizationContext? _uiContext;
    private Exception? _startupError;
    private int _disposed;
    private int _count;
    private long _totalByteCost;

    public PinnedScreenshotManager(
        long memoryBudgetBytes = DefaultMemoryBudgetBytes,
        long maximumSnapshotPixelCount = DefaultMaximumSnapshotPixelCount)
    {
        if (memoryBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        }

        if (maximumSnapshotPixelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshotPixelCount));
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("Pinned screenshots are only available on Windows.");
        }

        _memoryBudgetBytes = memoryBudgetBytes;
        _maximumSnapshotPixelCount = maximumSnapshotPixelCount;
        _uiThread = new Thread(RunUiLoop)
        {
            IsBackground = true,
            Name = "Crosio pinned screenshots",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _ready.Wait();
        if (_startupError is not null)
        {
            throw new PinnedScreenshotException(
                "The pinned screenshot message loop could not start.",
                _startupError);
        }
    }

    public int Count => Volatile.Read(ref _count);

    public long TotalByteCost => Interlocked.Read(ref _totalByteCost);

    public event EventHandler<Guid>? PinClosed;

    public Task<Guid> PinAsync(
        CapturedImage image,
        PinnedScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var eagerPng = image.CopyPngBytes();
        options ??= new PinnedScreenshotOptions();
        return InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = PinnedScreenshotSnapshot.Create(
                eagerPng,
                _maximumSnapshotPixelCount);
            var availableBytes = Math.Max(0, _memoryBudgetBytes - _totalByteCost);
            if (snapshot.ByteCost > availableBytes)
            {
                var requiredBytes = snapshot.ByteCost;
                snapshot.Dispose();
                throw new PinnedScreenshotException(
                    $"Not enough pinned screenshot memory is available. "
                    + $"Required: {requiredBytes:N0} bytes; available: {availableBytes:N0} bytes.");
            }

            var id = Guid.NewGuid();
            var anchor = options.Anchor ?? Cursor.Position;
            var screen = Screen.FromPoint(anchor);
            var dpiScale = Math.Max(1, NativeMethods.GetDpiForSystem() / 96f);
            var initialBounds = PinnedScreenshotLayout.InitialBounds(
                snapshot.SourcePixelSize,
                dpiScale,
                screen.WorkingArea,
                anchor);
            var window = new PinnedScreenshotWindow(
                snapshot,
                Rectangle.Round(initialBounds));
            try
            {
                window.Show();
                if (!window.IsExcludedFromCapture)
                {
                    throw new PinnedScreenshotException(
                        "Windows could not exclude the pinned screenshot from future captures.");
                }

                if (options.SelectForKeyboard)
                {
                    window.Activate();
                }
            }
            catch
            {
                window.Dispose();
                snapshot.Dispose();
                throw;
            }

            window.FormClosed += (_, _) => RemovePin(id);
            _pins.Add(id, new PinEntry(window, snapshot));
            Volatile.Write(ref _count, _pins.Count);
            Interlocked.Add(ref _totalByteCost, snapshot.ByteCost);
            return id;
        }, cancellationToken);
    }

    public Task<bool> CloseAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return InvokeAsync(() =>
        {
            if (!_pins.TryGetValue(id, out var pin))
            {
                return false;
            }

            pin.Window.Close();
            return true;
        }, cancellationToken);
    }

    public Task CloseAllAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync(() =>
        {
            foreach (var pin in _pins.Values.ToArray())
            {
                pin.Window.Close();
            }

            return true;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var context = _uiContext;
        if (context is null)
        {
            _ready.Dispose();
            return;
        }

        context.Post(_ =>
        {
            foreach (var pin in _pins.Values.ToArray())
            {
                pin.Window.Close();
            }

            Application.ExitThread();
        }, null);

        if (Thread.CurrentThread != _uiThread)
        {
            _ = _uiThread.Join(TimeSpan.FromSeconds(5));
        }

        _ready.Dispose();
    }

    private Task<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var context = _uiContext
            ?? throw new PinnedScreenshotException("The pinned screenshot message loop is unavailable.");
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(operation());
            }
            catch (OperationCanceledException cancelled)
            {
                completion.TrySetCanceled(cancelled.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }, null);
        return completion.Task;
    }

    private void RunUiLoop()
    {
        try
        {
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            _uiContext = context;
            _ready.Set();
            Application.Run();
        }
        catch (Exception error)
        {
            _startupError = error;
            _ready.Set();
        }
        finally
        {
            foreach (var pin in _pins.Values.ToArray())
            {
                pin.Window.Dispose();
                pin.Snapshot.Dispose();
            }

            _pins.Clear();
            Volatile.Write(ref _count, 0);
            Interlocked.Exchange(ref _totalByteCost, 0);
        }
    }

    private void RemovePin(Guid id)
    {
        if (!_pins.Remove(id, out var pin))
        {
            return;
        }

        var byteCost = pin.Snapshot.ByteCost;
        pin.Window.Dispose();
        pin.Snapshot.Dispose();
        Volatile.Write(ref _count, _pins.Count);
        Interlocked.Add(ref _totalByteCost, -byteCost);
        PinClosed?.Invoke(this, id);
    }

    private sealed record PinEntry(
        PinnedScreenshotWindow Window,
        PinnedScreenshotSnapshot Snapshot);
}

public interface IPinnedScreenshotService
{
    Task<Guid> PinAsync(
        CapturedImage image,
        PinnedScreenshotOptions? options = null,
        CancellationToken cancellationToken = default);
}
