using Crosio.Windows.Intelligence.Translation;
using Xunit;

namespace Crosio.Windows.Intelligence.Tests;

public sealed class TranslationCoordinatorTests
{
    [Fact]
    public async Task NewRequestCancelsOlderRequestAndOnlyLatestCanComplete()
    {
        var engine = new ControllableEngine();
        await using var coordinator = new TranslationCoordinator(engine);

        var first = coordinator.TranslateLatestAsync("first");
        await engine.WaitForRequestCountAsync(1);
        var second = coordinator.TranslateLatestAsync("second");
        await engine.WaitForRequestCountAsync(2);
        engine.Complete("second", "第二个");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var result = await second;
        Assert.Equal("第二个", result.TranslatedText);
        Assert.Equal("second", result.SourceText);
    }

    [Fact]
    public async Task UnsupportedInputNeverInvokesEngine()
    {
        var engine = new ControllableEngine();
        await using var coordinator = new TranslationCoordinator(engine);

        var error = await Assert.ThrowsAsync<TranslationException>(() => coordinator.TranslateLatestAsync("123"));

        Assert.Contains("无法判断", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, engine.RequestCount);
    }

    [Fact]
    public async Task DisposeCancelsAndWaitsForEveryInflightRequestBeforeCompleting()
    {
        var engine = new CancellationIgnoringEngine();
        var coordinator = new TranslationCoordinator(engine);

        var first = coordinator.TranslateLatestAsync("first");
        await engine.WaitForRequestCountAsync(1);
        var second = coordinator.TranslateLatestAsync("second");
        await engine.WaitForRequestCountAsync(2);

        Assert.True(engine.TokenFor("first").IsCancellationRequested);
        var dispose = coordinator.DisposeAsync().AsTask();
        await engine.WaitForAllCancellationAsync();

        Assert.False(dispose.IsCompleted);
        var cancellationCallbacks = 0;
        using var firstRegistration = engine.TokenFor("first").Register(() => cancellationCallbacks++);
        using var secondRegistration = engine.TokenFor("second").Register(() => cancellationCallbacks++);
        Assert.Equal(2, cancellationCallbacks);

        engine.Complete("first", "第一个");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(dispose.IsCompleted);

        engine.Complete("second", "第二个");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.TranslateLatestAsync("third"));
    }

    private sealed class ControllableEngine : ITextTranslationEngine
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<string>> _requests = [];

        public int RequestCount
        {
            get
            {
                lock (_gate)
                {
                    return _requests.Count;
                }
            }
        }

        public Task<string> TranslateAsync(
            string text,
            TranslationDirection direction,
            CancellationToken cancellationToken = default)
        {
            var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            lock (_gate)
            {
                _requests[text] = source;
            }
            return source.Task;
        }

        public void Complete(string text, string output)
        {
            lock (_gate)
            {
                _requests[text].TrySetResult(output);
            }
        }

        public async Task WaitForRequestCountAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (RequestCount < expected)
            {
                await Task.Delay(5, timeout.Token);
            }
        }
    }

    private sealed class CancellationIgnoringEngine : ITextTranslationEngine
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, PendingRequest> _requests = [];

        public Task<string> TranslateAsync(
            string text,
            TranslationDirection direction,
            CancellationToken cancellationToken = default)
        {
            var request = new PendingRequest(cancellationToken);
            lock (_gate)
            {
                _requests.Add(text, request);
            }
            return request.Completion.Task;
        }

        public CancellationToken TokenFor(string text)
        {
            lock (_gate)
            {
                return _requests[text].Token;
            }
        }

        public void Complete(string text, string output)
        {
            lock (_gate)
            {
                _requests[text].Completion.TrySetResult(output);
            }
        }

        public async Task WaitForRequestCountAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (true)
            {
                lock (_gate)
                {
                    if (_requests.Count >= expected)
                    {
                        return;
                    }
                }
                await Task.Delay(5, timeout.Token);
            }
        }

        public async Task WaitForAllCancellationAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (true)
            {
                lock (_gate)
                {
                    if (_requests.Count == 2 && _requests.Values.All(request => request.Token.IsCancellationRequested))
                    {
                        return;
                    }
                }
                await Task.Delay(5, timeout.Token);
            }
        }

        private sealed record PendingRequest(
            CancellationToken Token,
            TaskCompletionSource<string> Completion)
        {
            public PendingRequest(CancellationToken token)
                : this(token, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously))
            {
            }
        }
    }
}
