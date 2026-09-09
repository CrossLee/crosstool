namespace Crosio.Windows.Core.Activation;

public sealed class ActivationInbox
{
    private readonly object _gate = new();
    private readonly Queue<ActivationRoute> _pending = new();
    private Action<ActivationRoute>? _receiver;
    private bool _isDraining;

    public void Publish(ActivationRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        Action<ActivationRoute>? receiver;
        lock (_gate)
        {
            receiver = _receiver;
            if (receiver is null || _isDraining)
            {
                _pending.Enqueue(route);
                return;
            }
        }

        receiver(route);
    }

    public IDisposable Connect(Action<ActivationRoute> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        lock (_gate)
        {
            if (_receiver is not null)
            {
                throw new InvalidOperationException("An activation receiver is already connected.");
            }

            _receiver = receiver;
            _isDraining = true;
        }

        try
        {
            while (true)
            {
                ActivationRoute route;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        _isDraining = false;
                        break;
                    }

                    route = _pending.Dequeue();
                }

                receiver(route);
            }
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_receiver, receiver))
                {
                    _receiver = null;
                }

                _isDraining = false;
            }

            throw;
        }

        return new Subscription(this, receiver);
    }

    private void Disconnect(Action<ActivationRoute> receiver)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_receiver, receiver))
            {
                _receiver = null;
                _isDraining = false;
            }
        }
    }

    private sealed class Subscription(ActivationInbox owner, Action<ActivationRoute> receiver) : IDisposable
    {
        private ActivationInbox? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Disconnect(receiver);
        }
    }
}
