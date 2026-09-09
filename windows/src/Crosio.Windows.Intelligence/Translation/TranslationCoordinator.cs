namespace Crosio.Windows.Intelligence.Translation;

public sealed class TranslationCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ITextTranslationEngine _engine;
    private readonly HashSet<TranslationRequest> _inFlight = [];
    private TranslationRequest? _activeRequest;
    private TaskCompletionSource? _drained;
    private Task? _disposeTask;
    private bool _disposeStarted;
    private long _generation;

    public TranslationCoordinator(ITextTranslationEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public async Task<TranslationResult> TranslateLatestAsync(
        string? text,
        CancellationToken cancellationToken = default)
    {
        var input = TranslationDirectionDetector.Analyze(text);
        if (!input.CanTranslate)
        {
            throw new TranslationException(input.Issue switch
            {
                TranslationInputIssue.Empty => "请先输入或选中要翻译的文字",
                _ => "当前内容无法判断为中文或英文",
            });
        }

        TranslationRequest request;
        TranslationRequest? previous;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            previous = _activeRequest;
            request = new TranslationRequest(cancellationToken);
            _inFlight.Add(request);
            _activeRequest = request;
            generation = ++_generation;
        }
        previous?.Cancel();

        try
        {
            var output = await _engine.TranslateAsync(
                input.Text,
                input.Direction!.Value,
                request.Token).ConfigureAwait(false);

            lock (_gate)
            {
                if (generation != _generation || request.IsCancellationRequested)
                {
                    throw new OperationCanceledException(request.Token);
                }
            }

            var cleaned = output.Trim();
            if (cleaned.Length == 0)
            {
                throw new TranslationException("本地翻译没有返回内容");
            }

            return new TranslationResult(
                input.Text,
                cleaned,
                input.Direction.Value,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (_gate)
            {
                _inFlight.Remove(request);
                if (ReferenceEquals(_activeRequest, request))
                {
                    _activeRequest = null;
                }
                if (_disposeStarted && _inFlight.Count == 0)
                {
                    drained = _drained;
                }
            }
            request.Dispose();
            drained?.TrySetResult();
        }
    }

    public void Cancel()
    {
        TranslationRequest? active;
        lock (_gate)
        {
            if (_disposeStarted)
            {
                return;
            }
            _generation++;
            active = _activeRequest;
            _activeRequest = null;
        }
        active?.Cancel();
    }

    public ValueTask DisposeAsync()
    {
        TranslationRequest[] requests;
        Task disposeTask;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposeStarted = true;
            _generation++;
            _activeRequest = null;
            requests = [.. _inFlight];
            if (requests.Length == 0)
            {
                disposeTask = Task.CompletedTask;
            }
            else
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                disposeTask = _drained.Task;
            }
            _disposeTask = disposeTask;
        }

        // Cancellation is deliberately outside the coordinator lock: an engine
        // callback is allowed to finish synchronously and unregister itself.
        // Each request owns and disposes its CTS only after the engine call has
        // returned, so no other thread can tear down token state still used by
        // ONNX Runtime.
        foreach (var request in requests)
        {
            request.Cancel();
        }
        return new ValueTask(disposeTask);
    }

    private sealed class TranslationRequest : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation;
        private bool _disposed;

        public TranslationRequest(CancellationToken cancellationToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public CancellationToken Token => _cancellation.Token;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public void Cancel()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                try
                {
                    _cancellation.Cancel();
                }
                catch (AggregateException)
                {
                    // Cancellation callbacks are external to the coordinator. A
                    // faulty callback must not prevent the remaining requests from
                    // being cancelled and drained during shutdown.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }
}
