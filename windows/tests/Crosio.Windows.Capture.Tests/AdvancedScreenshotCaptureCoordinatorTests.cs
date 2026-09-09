using Crosio.Windows.Capture.Clipboard;
using Crosio.Windows.Capture.Composition;

namespace Crosio.Windows.Capture.Tests;

public sealed class AdvancedScreenshotCaptureCoordinatorTests
{
    private static readonly CapturedImage Source = Image(100, 60, 1);
    private static readonly CapturedImage Final = Image(140, 100, 2);

    [Fact]
    public async Task FramedCaptureCopiesOnlyAfterFinalFrameExists()
    {
        var events = new List<string>();
        var coordinator = Create(events);
        var target = new DisplayCaptureTarget(
            "DISPLAY1",
            new PixelRect(0, 0, 100, 60));

        var delivery = await coordinator.CaptureFramedAndCopyAsync(target);
        events.Add("editor");

        Assert.Same(Final, delivery.Image);
        Assert.Equal(["capture-display", "compose-frame", "clipboard-final", "editor"], events);
    }

    [Fact]
    public async Task MultiWindowCaptureUsesOnlyExplicitSelectionBeforeFinalCopy()
    {
        var events = new List<string>();
        var coordinator = Create(events);
        var windows = new[]
        {
            new CaptureWindow(new nint(11), "One", 1, new PixelRect(0, 0, 100, 60)),
            new CaptureWindow(new nint(22), "Two", 2, new PixelRect(100, 0, 100, 60)),
        };

        var delivery = await coordinator.CaptureSelectedWindowsAndCopyAsync(windows);
        events.Add("editor");

        Assert.NotNull(delivery);
        Assert.Same(Final, delivery.Image);
        Assert.Equal(
            ["capture-window-11", "capture-window-22", "compose-windows-2", "clipboard-final", "editor"],
            events);
    }

    [Fact]
    public async Task CancelledMultiWindowSelectionDoesNotFallBackToScreen()
    {
        var events = new List<string>();
        var coordinator = Create(events);

        var delivery = await coordinator.CaptureSelectedWindowsAndCopyAsync(null);

        Assert.Null(delivery);
        Assert.Empty(events);
    }

    private static AdvancedScreenshotCaptureCoordinator Create(List<string> events)
    {
        var screenCapture = new FakeScreenCapture(events);
        return new AdvancedScreenshotCaptureCoordinator(
            screenCapture,
            new FakeWindowCapture(events),
            new FakeComposer(events),
            new ScreenshotCapturePipeline(screenCapture, new FakeClipboard(events)));
    }

    private static CapturedImage Image(int width, int height, byte marker) => new(
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker],
        width,
        height);

    private sealed class FakeScreenCapture(List<string> events) : IScreenshotCaptureService
    {
        public ScreenshotCaptureCapabilities Capabilities { get; } = new(
            true,
            true,
            true,
            true,
            false,
            false,
            false);

        public Task<CapturedImage> CaptureAsync(
            ScreenshotCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("capture-display");
            return Task.FromResult(Source);
        }
    }

    private sealed class FakeWindowCapture(List<string> events) : ISelectedWindowCaptureService
    {
        public Task<CapturedImage> CaptureAsync(
            CaptureWindow selectedWindow,
            CancellationToken cancellationToken = default)
        {
            events.Add($"capture-window-{selectedWindow.WindowHandle}");
            return Task.FromResult(Source);
        }
    }

    private sealed class FakeComposer(List<string> events) : IScreenshotComposer
    {
        public CapturedImage AddCrosioDeviceFrame(CapturedImage source)
        {
            events.Add("compose-frame");
            return Final;
        }

        public CapturedImage ComposeSelectedWindows(
            IReadOnlyList<WindowCompositeItem> selectedWindows)
        {
            events.Add($"compose-windows-{selectedWindows.Count}");
            return Final;
        }
    }

    private sealed class FakeClipboard(List<string> events) : IImageClipboard
    {
        public Task WritePngAsync(
            CapturedImage image,
            CancellationToken cancellationToken = default)
        {
            Assert.Same(Final, image);
            events.Add("clipboard-final");
            return Task.CompletedTask;
        }
    }
}
