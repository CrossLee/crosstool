using Crosio.Windows.Intelligence.Translation;
using Xunit;

namespace Crosio.Windows.Translation.Tests;

public sealed class OfflineTranslationEngineTests
{
    [Fact]
    public async Task MissingModelIsExplicitAndNeverReturnsFakeTranslation()
    {
        using var root = new TemporaryModelRoot();
        await using var engine = new MarianOnnxTranslationEngine(new OfflineModelCatalog(root.Path));

        var error = await Assert.ThrowsAsync<TranslationModelMissingException>(
            () => engine.TranslateAsync("Hello", TranslationDirection.EnglishToChinese));

        Assert.Equal(TranslationDirection.EnglishToChinese, error.Direction);
        Assert.Contains("尚未安装", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRuntimeEvictionWaitsForEveryConcurrentLeaseBeforeDisposing()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        var package = TestModelPack.Create();
        await using (var installer = new OfflineModelPackInstaller(catalog))
        {
            await installer.InstallArchiveAsync(new MemoryStream(package.Bytes), package.Descriptor);
        }
        var runtime = new ControlledRuntime();
        await using var engine = new MarianOnnxTranslationEngine(
            catalog,
            (_, _) => Task.FromResult<IMarianTranslationRuntime>(runtime));

        var first = engine.TranslateAsync("first", package.Manifest.Direction);
        await runtime.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = engine.TranslateAsync("second", package.Manifest.Direction);
        await runtime.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runtime.FailFirst(new TranslationModelInvalidException("simulated runtime failure"));
        await Assert.ThrowsAsync<TranslationModelInvalidException>(() => first);
        Assert.Equal(0, runtime.DisposeCount);

        var disposal = engine.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.TranslateAsync("third", package.Manifest.Direction));

        runtime.CompleteSecond("translated");
        Assert.Equal("translated", await second);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public void GreedyDecoderHonorsForcedBeginningToken()
    {
        var generation = Generation() with { ForcedBeginningOfSentenceTokenId = 3 };

        var selected = GreedyTokenSelector.Select([100, 90, 80, -100], 0, generation);

        Assert.Equal(3, selected);
    }

    [Fact]
    public void GreedyDecoderSuppressesEarlyEndAndPadding()
    {
        var generation = Generation();

        var first = GreedyTokenSelector.Select([100, 5, 90, 10], 0, generation);
        var second = GreedyTokenSelector.Select([100, 5, 90, 10], 1, generation);

        Assert.Equal(3, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public void GreedyDecoderRejectsAllInvalidCandidates()
    {
        var generation = Generation() with { SuppressedTokenIds = [1, 3] };

        Assert.Throws<TranslationModelInvalidException>(
            () => GreedyTokenSelector.Select([1, 2, 3, 4], 0, generation));
    }

    private static MarianGenerationManifest Generation() => new()
    {
        UnknownTokenId = 1,
        EndOfSentenceTokenId = 0,
        PaddingTokenId = 2,
        DecoderStartTokenId = 2,
        MaxInputTokens = 512,
        MaxOutputTokens = 512,
        MinimumOutputTokens = 1,
    };

    private sealed class ControlledRuntime : IMarianTranslationRuntime
    {
        private readonly TaskCompletionSource<string> _firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _secondCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            if (text == "first")
            {
                FirstStarted.TrySetResult();
                return _firstCompletion.Task;
            }
            if (text == "second")
            {
                SecondStarted.TrySetResult();
                return _secondCompletion.Task;
            }
            throw new InvalidOperationException($"Unexpected input: {text}");
        }

        public void FailFirst(Exception error) => _firstCompletion.TrySetException(error);

        public void CompleteSecond(string result) => _secondCompletion.TrySetResult(result);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
