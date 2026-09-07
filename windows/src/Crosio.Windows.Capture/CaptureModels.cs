using System.Collections.ObjectModel;

namespace Crosio.Windows.Capture;

/// <summary>
/// A rectangle in virtual-desktop physical pixels. Left and top may be
/// negative when a monitor is arranged above or to the left of the primary
/// monitor.
/// </summary>
public readonly record struct PixelRect
{
    public PixelRect(int left, int top, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => checked(Left + Width);

    public int Bottom => checked(Top + Height);

    public long PixelCount => checked((long)Width * Height);
}

public enum ScreenshotCaptureKind
{
    VirtualDesktop,
    Display,
    Window,
    Region,
}

/// <summary>
/// A concrete capture source. The caller chooses the target explicitly; the
/// service never broadens a region or window request to the whole desktop.
/// </summary>
public abstract record ScreenshotCaptureTarget(ScreenshotCaptureKind Kind);

public sealed record VirtualDesktopCaptureTarget()
    : ScreenshotCaptureTarget(ScreenshotCaptureKind.VirtualDesktop);

public sealed record DisplayCaptureTarget(string DeviceName, PixelRect Bounds)
    : ScreenshotCaptureTarget(ScreenshotCaptureKind.Display)
{
    public string DeviceName { get; } = string.IsNullOrWhiteSpace(DeviceName)
        ? throw new ArgumentException("A display device name is required.", nameof(DeviceName))
        : DeviceName;
}

/// <summary>
/// Identifies a top-level window. The first desktop-composition backend
/// captures the window rectangle exactly as it is visible on screen; pixels
/// covered by another window remain covered. Inspect the capture service's
/// capabilities before promising occluded-window capture.
/// </summary>
public sealed record WindowCaptureTarget(nint WindowHandle)
    : ScreenshotCaptureTarget(ScreenshotCaptureKind.Window)
{
    public nint WindowHandle { get; } = WindowHandle != nint.Zero
        ? WindowHandle
        : throw new ArgumentException("A non-zero window handle is required.", nameof(WindowHandle));
}

public sealed record RegionCaptureTarget(PixelRect Bounds)
    : ScreenshotCaptureTarget(ScreenshotCaptureKind.Region);

public sealed record ScreenshotCaptureRequest(
    ScreenshotCaptureTarget Target,
    bool IncludeCursor = false,
    long MaximumPixelCount = WindowsScreenshotCaptureService.DefaultMaximumPixelCount)
{
    public ScreenshotCaptureTarget Target { get; } = Target
        ?? throw new ArgumentNullException(nameof(Target));

    public long MaximumPixelCount { get; } = MaximumPixelCount > 0
        ? MaximumPixelCount
        : throw new ArgumentOutOfRangeException(
            nameof(MaximumPixelCount),
            "The capture pixel limit must be positive.");
}

/// <summary>
/// An eager, self-contained PNG snapshot. No file URL or mutable native image
/// remains behind, so the clipboard and pin can outlive a temporary draft.
/// </summary>
public sealed class CapturedImage
{
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly byte[] _pngBytes;

    public CapturedImage(ReadOnlySpan<byte> pngBytes, int pixelWidth, int pixelHeight)
    {
        if (!pngBytes.StartsWith(PngSignature))
        {
            throw new ArgumentException("The image must contain eager PNG bytes.", nameof(pngBytes));
        }

        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        _pngBytes = pngBytes.ToArray();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public ReadOnlyMemory<byte> PngBytes => _pngBytes;

    public byte[] CopyPngBytes() => (byte[])_pngBytes.Clone();
}

public sealed record CaptureDisplay(
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkingArea,
    bool IsPrimary);

public sealed record CaptureWindow(
    nint WindowHandle,
    string Title,
    uint ProcessId,
    PixelRect Bounds);

public sealed class CaptureTargetSnapshot
{
    public CaptureTargetSnapshot(
        IEnumerable<CaptureDisplay> displays,
        IEnumerable<CaptureWindow> windows)
    {
        Displays = new ReadOnlyCollection<CaptureDisplay>(
            (displays ?? throw new ArgumentNullException(nameof(displays))).ToArray());
        Windows = new ReadOnlyCollection<CaptureWindow>(
            (windows ?? throw new ArgumentNullException(nameof(windows))).ToArray());
    }

    public IReadOnlyList<CaptureDisplay> Displays { get; }

    public IReadOnlyList<CaptureWindow> Windows { get; }
}

public interface IScreenshotCaptureService
{
    ScreenshotCaptureCapabilities Capabilities { get; }

    Task<CapturedImage> CaptureAsync(
        ScreenshotCaptureRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ScreenshotCaptureCapabilities(
    bool SupportsVirtualDesktop,
    bool SupportsDisplay,
    bool SupportsWindow,
    bool SupportsRegion,
    bool SupportsCursor,
    bool CapturesOccludedWindowContents,
    bool CapturesMinimizedWindowContents);

public interface ICaptureTargetCatalog
{
    CaptureTargetSnapshot GetSnapshot(uint? excludedProcessId = null);
}

/// <summary>
/// The App owns the visual picker. This boundary keeps selection and capture
/// separate so cancellation never silently turns into a whole-screen capture.
/// </summary>
public interface ICaptureTargetPicker
{
    Task<DisplayCaptureTarget?> PickDisplayAsync(CancellationToken cancellationToken = default);

    Task<WindowCaptureTarget?> PickWindowAsync(CancellationToken cancellationToken = default);

    Task<RegionCaptureTarget?> PickRegionAsync(CancellationToken cancellationToken = default);
}
