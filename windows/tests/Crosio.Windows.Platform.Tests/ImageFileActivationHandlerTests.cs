using Crosio.Windows.Platform.Images;
using Crosio.Windows.Platform.Shell;

namespace Crosio.Windows.Platform.Tests;

public sealed class ImageFileActivationHandlerTests
{
    [Fact]
    public async Task SuccessfulCompressionRevealsOutputsOnceAndKeepsWindowHidden()
    {
        var source = @"C:\Pictures\photo.JPG";
        var output = @"C:\Pictures\photo-crosio.JPG";
        var compressor = new FakeCompressor(path => ImageCompressionOutcome.Compressed(
            new ImageCompressionResult(path, output, 1_000, 400, 100, 80, true)));
        var revealer = new RecordingRevealer();
        var handler = new ImageFileActivationHandler(compressor, revealer);

        var result = await handler.HandleAsync(
            [source, source],
            new ImageCompressionSettings());

        Assert.False(result.ShouldShowMainWindow);
        Assert.Single(result.Items);
        Assert.Equal(new[] { output }, result.RevealedOutputPaths);
        Assert.Equal(new[] { output }, Assert.Single(revealer.Batches));
    }

    [Fact]
    public async Task AlreadyOptimizedImageDoesNotOpenExplorer()
    {
        var compressor = new FakeCompressor(
            _ => ImageCompressionOutcome.AlreadyOptimized(1_000));
        var revealer = new RecordingRevealer();
        var handler = new ImageFileActivationHandler(compressor, revealer);

        var result = await handler.HandleAsync(
            [@"C:\Pictures\small.png"],
            new ImageCompressionSettings());

        Assert.False(result.ShouldShowMainWindow);
        Assert.Single(result.Items);
        Assert.Empty(result.RevealedOutputPaths);
        Assert.Empty(revealer.Batches);
    }

    [Fact]
    public async Task UnsupportedFileIsReportedWithoutCallingCompressor()
    {
        var compressor = new FakeCompressor(
            _ => throw new InvalidOperationException("Should not run"));
        var revealer = new RecordingRevealer();
        var handler = new ImageFileActivationHandler(compressor, revealer);

        var result = await handler.HandleAsync(
            [@"C:\Pictures\animation.gif"],
            new ImageCompressionSettings());

        var item = Assert.Single(result.Items);
        Assert.False(item.Succeeded);
        Assert.NotNull(item.ErrorMessage);
        Assert.Equal(0, compressor.CallCount);
        Assert.Empty(revealer.Batches);
    }

    [Fact]
    public async Task ConcurrentBatchesUseOneAppWideCompressionSlot()
    {
        var compressor = new BlockingCompressor();
        var revealer = new RecordingRevealer();
        var handler = new ImageFileActivationHandler(compressor, revealer);

        var first = handler.HandleAsync(
            [@"C:\Pictures\first.jpg"],
            new ImageCompressionSettings());
        await compressor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = handler.HandleAsync(
            [@"C:\Pictures\second.jpg"],
            new ImageCompressionSettings());

        Assert.Equal(1, compressor.CallCount);
        Assert.False(second.IsCompleted);
        compressor.ReleaseFirst();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, compressor.CallCount);
        Assert.Equal(1, compressor.MaximumConcurrentCalls);
        Assert.All(results, result => Assert.False(result.ShouldShowMainWindow));
        Assert.Equal(2, revealer.Batches.Count);
    }

    [Fact]
    public async Task CancellingWhileWaitingNeverCallsCompressorForThatBatch()
    {
        var compressor = new BlockingCompressor();
        var handler = new ImageFileActivationHandler(compressor, new RecordingRevealer());
        var first = handler.HandleAsync(
            [@"C:\Pictures\first.jpg"],
            new ImageCompressionSettings());
        await compressor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();

        var waiting = handler.HandleAsync(
            [@"C:\Pictures\cancelled.jpg"],
            new ImageCompressionSettings(),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, compressor.CallCount);
        compressor.ReleaseFirst();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GateIsReleasedWhenExplorerRevealFails()
    {
        var compressor = new FakeCompressor(path => ImageCompressionOutcome.Compressed(
            new ImageCompressionResult(
                path,
                $"{path}.compressed.jpg",
                1_000,
                400,
                100,
                80,
                true)));
        var revealer = new FailFirstRevealer();
        var handler = new ImageFileActivationHandler(compressor, revealer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            [@"C:\Pictures\first.jpg"],
            new ImageCompressionSettings()));
        var second = await handler.HandleAsync(
                [@"C:\Pictures\second.jpg"],
                new ImageCompressionSettings())
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(second.Items);
        Assert.True(second.Items[0].Succeeded);
        Assert.Equal(2, compressor.CallCount);
        Assert.Equal(2, revealer.CallCount);
    }

    private sealed class FakeCompressor(
        Func<string, ImageCompressionOutcome> resultFactory) : IImageCompressionService
    {
        public int CallCount { get; private set; }

        public Task<ImageCompressionOutcome> CompressAsync(
            string sourcePath,
            ImageCompressionSettings settings,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(resultFactory(sourcePath));
        }
    }

    private sealed class RecordingRevealer : IExplorerRevealService
    {
        public List<IReadOnlyList<string>> Batches { get; } = [];

        public void RevealFiles(IReadOnlyList<string> paths)
        {
            Batches.Add(paths.ToArray());
        }
    }

    private sealed class BlockingCompressor : IImageCompressionService
    {
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;
        private int _callCount;
        private int _maximumConcurrentCalls;

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrentCalls);

        public async Task<ImageCompressionOutcome> CompressAsync(
            string sourcePath,
            ImageCompressionSettings settings,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(active);
            try
            {
                if (call == 1)
                {
                    FirstStarted.TrySetResult();
                    await _releaseFirst.Task.WaitAsync(cancellationToken);
                }
                return ImageCompressionOutcome.Compressed(new ImageCompressionResult(
                    sourcePath,
                    $"{sourcePath}.compressed.jpg",
                    1_000,
                    400,
                    100,
                    80,
                    true));
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentCalls);
                if (candidate <= current ||
                    Interlocked.CompareExchange(ref _maximumConcurrentCalls, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FailFirstRevealer : IExplorerRevealService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public void RevealFiles(IReadOnlyList<string> paths)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                throw new InvalidOperationException("simulated Explorer failure");
            }
        }
    }
}
