namespace Crosio.Windows.Translation;

/// <summary>
/// Closes a service to new work, cancels every operation already admitted,
/// and completes disposal only after those operations have left their scopes.
/// </summary>
internal sealed class AsyncOperationLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource? _drained;
    private Task? _disposeTask;
    private int _activeOperations;
    private bool _disposeStarted;

    public Operation Enter(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token);
            _activeOperations++;
            return new Operation(this, linked);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task drain;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }
            _disposeStarted = true;
            drain = _activeOperations == 0
                ? Task.CompletedTask
                : (_drained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = DisposeCoreAsync(drain, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(Task drain, TaskCompletionSource completion)
    {
        Exception? cancellationError = null;
        try
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (Exception error)
            {
                cancellationError = error;
            }
            await drain.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
            if (cancellationError is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(cancellationError);
            }
        }
    }

    private void Exit()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (_activeOperations <= 0)
            {
                throw new InvalidOperationException("异步操作计数无效");
            }
            _activeOperations--;
            if (_disposeStarted && _activeOperations == 0)
            {
                drained = _drained;
            }
        }
        drained?.TrySetResult();
    }

    internal sealed class Operation : IDisposable
    {
        private AsyncOperationLifetime? _owner;
        private CancellationTokenSource? _cancellation;

        internal Operation(
            AsyncOperationLifetime owner,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
        }

        public CancellationToken Token =>
            Volatile.Read(ref _cancellation)?.Token ??
            throw new ObjectDisposedException(nameof(Operation));

        public void Dispose()
        {
            var cancellation = Interlocked.Exchange(ref _cancellation, null);
            var owner = Interlocked.Exchange(ref _owner, null);
            if (cancellation is null || owner is null)
            {
                return;
            }
            cancellation.Dispose();
            owner.Exit();
        }
    }
}
