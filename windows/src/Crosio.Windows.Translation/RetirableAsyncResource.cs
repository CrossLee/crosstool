namespace Crosio.Windows.Translation;

/// <summary>
/// Reference-counted lifetime for a lazily loaded native-backed resource.
/// Retiring prevents new leases immediately, but disposal is deferred until
/// every caller that could still be using the resource has exited.
/// </summary>
internal sealed class RetirableAsyncResource<T> where T : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Lazy<Task<T>> _resource;
    private readonly TaskCompletionSource _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _leaseCount;
    private bool _retired;
    private bool _cleanupStarted;

    public RetirableAsyncResource(Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _resource = new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool TryAcquire(out Lease? lease)
    {
        lock (_gate)
        {
            if (_retired)
            {
                lease = null;
                return false;
            }
            _leaseCount++;
            lease = new Lease(this);
            return true;
        }
    }

    public Task<T> GetValueAsync(CancellationToken cancellationToken) =>
        _resource.Value.WaitAsync(cancellationToken);

    public Task RetireAsync()
    {
        var startCleanup = false;
        lock (_gate)
        {
            _retired = true;
            if (_leaseCount == 0 && !_cleanupStarted)
            {
                _cleanupStarted = true;
                startCleanup = true;
            }
        }
        if (startCleanup)
        {
            _ = DisposeResourceAsync();
        }
        return _disposed.Task;
    }

    private void Release()
    {
        var startCleanup = false;
        lock (_gate)
        {
            if (_leaseCount <= 0)
            {
                throw new InvalidOperationException("资源租约计数无效");
            }
            _leaseCount--;
            if (_retired && _leaseCount == 0 && !_cleanupStarted)
            {
                _cleanupStarted = true;
                startCleanup = true;
            }
        }
        if (startCleanup)
        {
            _ = DisposeResourceAsync();
        }
    }

    private async Task DisposeResourceAsync()
    {
        try
        {
            if (_resource.IsValueCreated)
            {
                var resource = await _resource.Value.ConfigureAwait(false);
                resource.Dispose();
            }
        }
        catch
        {
            // A failed/cancelled factory either created no resource or owns the
            // cleanup of any partially-created native handles. Disposal must
            // still complete so engine shutdown cannot deadlock.
        }
        finally
        {
            _disposed.TrySetResult();
        }
    }

    internal sealed class Lease : IDisposable
    {
        private RetirableAsyncResource<T>? _owner;

        internal Lease(RetirableAsyncResource<T> owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
