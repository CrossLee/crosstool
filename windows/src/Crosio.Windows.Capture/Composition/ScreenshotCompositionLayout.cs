namespace Crosio.Windows.Capture.Composition;

public readonly record struct FramedScreenshotLayout(
    int CanvasWidth,
    int CanvasHeight,
    PixelRect LidBounds,
    PixelRect ScreenBounds,
    PixelRect BaseBounds,
    int OuterPadding)
{
    public const long MaximumPixelCount = 50_000_000;

    public static FramedScreenshotLayout Create(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                sourceWidth <= 0 ? nameof(sourceWidth) : nameof(sourceHeight));
        }

        var sideBezel = Math.Max(22, Round(sourceWidth * 0.027));
        var topBezel = Math.Max(24, Round(sourceWidth * 0.030));
        var bottomBezel = Math.Max(30, Round(sourceWidth * 0.040));
        var baseHeight = Math.Max(42, Round(sourceWidth * 0.070));
        var outerPadding = Math.Max(24, Round(sourceWidth * 0.035));
        var lidWidth = checked(sourceWidth + sideBezel * 2);
        var lidHeight = checked(sourceHeight + topBezel + bottomBezel);
        var canvasWidth = checked(lidWidth + outerPadding * 2);
        var canvasHeight = checked(lidHeight + baseHeight + outerPadding * 2);
        if (canvasWidth > 32_768
            || canvasHeight > 32_768
            || checked((long)canvasWidth * canvasHeight) > MaximumPixelCount)
        {
            throw new ScreenshotCaptureException("带壳截图尺寸超过安全限制。");
        }

        var lid = new PixelRect(outerPadding, outerPadding, lidWidth, lidHeight);
        var screen = new PixelRect(
            lid.Left + sideBezel,
            lid.Top + topBezel,
            sourceWidth,
            sourceHeight);
        var deviceBase = new PixelRect(
            Round(outerPadding * 0.42),
            lid.Bottom,
            canvasWidth - Round(outerPadding * 0.84),
            baseHeight);
        return new FramedScreenshotLayout(
            canvasWidth,
            canvasHeight,
            lid,
            screen,
            deviceBase,
            outerPadding);
    }

    private static int Round(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
}

public sealed record WindowCompositeItem(
    CapturedImage Image,
    PixelRect DesktopBounds,
    string Title);

public sealed record WindowCompositePlacement(
    WindowCompositeItem Item,
    PixelRect DestinationBounds);

public sealed record MultiWindowCompositeLayout(
    int CanvasWidth,
    int CanvasHeight,
    IReadOnlyList<WindowCompositePlacement> Placements)
{
    public const int Padding = 28;
    public const long MaximumPixelCount = 80_000_000;

    public static MultiWindowCompositeLayout Create(
        IReadOnlyList<WindowCompositeItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("至少需要选择一个窗口。", nameof(items));
        }

        var left = items.Min(item => item.DesktopBounds.Left);
        var top = items.Min(item => item.DesktopBounds.Top);
        var right = items.Max(item => item.DesktopBounds.Right);
        var bottom = items.Max(item => item.DesktopBounds.Bottom);
        var width = checked(right - left + Padding * 2);
        var height = checked(bottom - top + Padding * 2);
        if (width > 32_768
            || height > 32_768
            || checked((long)width * height) > MaximumPixelCount)
        {
            throw new ScreenshotCaptureException("所选窗口分布范围过大，无法安全合成。");
        }

        var placements = items.Select(item => new WindowCompositePlacement(
            item,
            new PixelRect(
                checked(item.DesktopBounds.Left - left + Padding),
                checked(item.DesktopBounds.Top - top + Padding),
                item.Image.PixelWidth,
                item.Image.PixelHeight)))
            .ToArray();
        return new MultiWindowCompositeLayout(width, height, placements);
    }
}
