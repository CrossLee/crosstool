using System.Collections.Concurrent;

namespace Crosio.Windows.Media;

/// <summary>
/// Executes potentially blocking native calls on one dedicated background
/// thread. Calls are strictly ordered, and enqueueing never waits for the
/// native operation to return.
/// </summary>
internal sealed class DedicatedNativeCallWorker : IDisposable
{
    private readonly BlockingCollection<IWorkItem> _queue = new();
    private readonly object _enqueueSync = new();
    private readonly Thread _thread;
    private bool _completionRequested;

    public DedicatedNativeCallWorker(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
        };
        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.MTA);
        }

        _thread.Start();
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync<object?>(() =>
        {
            action();
            return null;
        });
    }

    public Task<T> InvokeAsync<T>(Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var item = new WorkItem<T>(callback);
        lock (_enqueueSync)
        {
            if (_completionRequested)
            {
                item.Reject(CreateCompletedException());
                return item.Task;
            }

            try
            {
                _queue.Add(item);
            }
            catch (ObjectDisposedException)
            {
                item.Reject(CreateCompletedException());
            }
            catch (InvalidOperationException)
            {
                item.Reject(CreateCompletedException());
            }
        }
        return item.Task;
    }

    /// <summary>
    /// Stops accepting work and lets the background thread drain anything
    /// already queued. This method deliberately never joins the thread: a
    /// wedged native call must not become a process-shutdown hang.
    /// </summary>
    public void Complete()
    {
        lock (_enqueueSync)
        {
            if (_completionRequested)
            {
                return;
            }

            _completionRequested = true;
            _queue.CompleteAdding();
        }
    }

    public void Dispose() => Complete();

    private void Run()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Execute();
            }
        }
        finally
        {
            var exception = CreateCompletedException();
            while (_queue.TryTake(out var pending))
            {
                pending.Reject(exception);
            }

            _queue.Dispose();
        }
    }

    private static ObjectDisposedException CreateCompletedException() =>
        new(nameof(DedicatedNativeCallWorker), "The native-call worker is completing.");

    private interface IWorkItem
    {
        void Execute();

        void Reject(Exception exception);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> _callback;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<T> callback)
        {
            _callback = callback;
        }

        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            try
            {
                _completion.TrySetResult(_callback());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Reject(Exception exception) => _completion.TrySetException(exception);
    }
}

/// <summary>
/// The testable lifecycle boundary used by every ScreenRecorderLib recording
/// session. It keeps create, start, stop and dispose on the same MTA worker.
/// </summary>
internal sealed class NativeRecordingSessionWorker : IDisposable
{
    private readonly DedicatedNativeCallWorker _worker;
    private int _completionRequested;

    public NativeRecordingSessionWorker(string name)
    {
        _worker = new DedicatedNativeCallWorker(name);
    }

    public Task<T> CreateAsync<T>(Func<T> create) => _worker.InvokeAsync(create);

    public Task StartAsync(Action start) => _worker.InvokeAsync(start);

    public Task StopAsync(Action stop) => _worker.InvokeAsync(stop);

    public Task DisposeNativeAsync(Action dispose)
    {
        var task = _worker.InvokeAsync(dispose);
        Complete();
        return task;
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completionRequested, 1) == 0)
        {
            _worker.Complete();
        }
    }

    public void Dispose() => Complete();
}
