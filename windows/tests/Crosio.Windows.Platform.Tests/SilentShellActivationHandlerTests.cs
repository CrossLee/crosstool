using Crosio.Windows.Platform.Images;
using Crosio.Windows.Platform.Shell;

namespace Crosio.Windows.Platform.Tests;

public sealed class SilentShellActivationHandlerTests
{
    [Fact]
    public async Task CopyPathCopiesDirectlyAndNeverRequestsMainWindow()
    {
        var clipboard = new RecordingClipboard();
        var compressor = new RecordingCompressor();
        var revealer = new RecordingRevealer();
        var handler = CreateHandler(clipboard, compressor, revealer);
        var paths = new[] { @"C:\Course\notes.txt", @"C:\Course\Assets" };

        var result = await handler.HandleAsync(
            new FileActivationRequest(FileActivationOperation.CopyPaths, paths, []),
            new ImageCompressionSettings());

        Assert.False(result.ShouldShowMainWindow);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(paths, Assert.Single(clipboard.Calls));
        Assert.Empty(compressor.Paths);
        Assert.Empty(revealer.Batches);
    }

    [Fact]
    public async Task BackgroundStartupDoesNotTouchClipboardCompressionOrWindow()
    {
        var clipboard = new RecordingClipboard();
        var compressor = new RecordingCompressor();
        var revealer = new RecordingRevealer();
        var handler = CreateHandler(clipboard, compressor, revealer);

        var result = await handler.HandleAsync(
            new FileActivationRequest(FileActivationOperation.BackgroundOnly, [], []),
            new ImageCompressionSettings());

        Assert.False(result.ShouldShowMainWindow);
        Assert.Empty(clipboard.Calls);
        Assert.Empty(compressor.Paths);
        Assert.Empty(revealer.Batches);
    }

    [Fact]
    public async Task ExplicitShowRequestsMainWindowWithoutRunningShellOperations()
    {
        var clipboard = new RecordingClipboard();
        var compressor = new RecordingCompressor();
        var revealer = new RecordingRevealer();
        var handler = CreateHandler(clipboard, compressor, revealer);

        var result = await handler.HandleAsync(
            new FileActivationRequest(FileActivationOperation.ShowMainWindow, [], []),
            new ImageCompressionSettings());

        Assert.True(result.ShouldShowMainWindow);
        Assert.Empty(clipboard.Calls);
        Assert.Empty(compressor.Paths);
        Assert.Empty(revealer.Batches);
    }

    private static SilentShellActivationHandler CreateHandler(
        IClipboardPathService clipboard,
        IImageCompressionService compressor,
        IExplorerRevealService revealer) => new(
            clipboard,
            new ImageFileActivationHandler(compressor, revealer));

    private sealed class RecordingClipboard : IClipboardPathService
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public void CopyPaths(IReadOnlyList<string> paths)
        {
            Calls.Add(paths.ToArray());
        }
    }

    private sealed class RecordingCompressor : IImageCompressionService
    {
        public List<string> Paths { get; } = [];

        public Task<ImageCompressionOutcome> CompressAsync(
            string sourcePath,
            ImageCompressionSettings settings,
            CancellationToken cancellationToken = default)
        {
            Paths.Add(sourcePath);
            return Task.FromResult(ImageCompressionOutcome.AlreadyOptimized(100));
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
}
