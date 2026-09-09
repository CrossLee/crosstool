using Crosio.Windows.Capture.Clipboard;

namespace Crosio.Windows.Capture.Tests;

public sealed class ScreenshotCapturePipelineTests
{
    private static readonly CapturedImage Image = new(
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
        100,
        50);

    [Fact]
    public async Task FinalImageIsCopiedBeforePipelineReturnsItToEditor()
    {
        var events = new List<string>();
        var pipeline = new ScreenshotCapturePipeline(
            new FakeCaptureService(events),
            new FakeClipboard(events));

        var result = await pipeline.CaptureAndCopyAsync(
            new ScreenshotCaptureRequest(new VirtualDesktopCaptureTarget()));
        events.Add("editor");

        Assert.True(result.WasCopiedToClipboard);
        Assert.Null(result.ClipboardError);
        Assert.Same(Image, result.Image);
        Assert.Equal(["capture", "clipboard", "editor"], events);
    }

    [Fact]
    public async Task ClipboardFailureDoesNotDiscardImageOrBlockEditorHandoff()
    {
        var events = new List<string>();
        var clipboardError = new ImageClipboardException("clipboard busy");
        var pipeline = new ScreenshotCapturePipeline(
            new FakeCaptureService(events),
            new FakeClipboard(events, clipboardError));

        var result = await pipeline.CaptureAndCopyAsync(
            new ScreenshotCaptureRequest(new VirtualDesktopCaptureTarget()));
        events.Add("editor");

        Assert.False(result.WasCopiedToClipboard);
        Assert.Same(clipboardError, result.ClipboardError);
        Assert.Same(Image, result.Image);
        Assert.Equal(["capture", "clipboard", "editor"], events);
    }

    [Fact]
    public async Task CompletedCompositeUsesTheSameClipboardFirstDelivery()
    {
        var events = new List<string>();
        var pipeline = new ScreenshotCapturePipeline(
            new FakeCaptureService(events),
            new FakeClipboard(events));

        var result = await pipeline.DeliverAndCopyAsync(Image);
        events.Add("editor");

        Assert.True(result.WasCopiedToClipboard);
        Assert.Same(Image, result.Image);
        Assert.Equal(["clipboard", "editor"], events);
    }

    private sealed class FakeCaptureService(List<string> events) : IScreenshotCaptureService
    {
        public ScreenshotCaptureCapabilities Capabilities { get; } = new(
            true,
            true,
            true,
            true,
            true,
            false,
            false);

        public Task<CapturedImage> CaptureAsync(
            ScreenshotCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("capture");
            return Task.FromResult(Image);
        }
    }

    private sealed class FakeClipboard(
        List<string> events,
        Exception? error = null) : IImageClipboard
    {
        public Task WritePngAsync(
            CapturedImage image,
            CancellationToken cancellationToken = default)
        {
            events.Add("clipboard");
            return error is null
                ? Task.CompletedTask
                : Task.FromException(error);
        }
    }
}
