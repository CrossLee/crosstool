namespace Crosio.Windows.Core.Presentation;

public readonly record struct DesktopRectangle(int X, int Y, int Width, int Height);

public static class WindowPlacementPolicy
{
    public static DesktopRectangle UseFallbackSize(
        DesktopRectangle windowBounds,
        int fallbackWidth,
        int fallbackHeight)
    {
        if (fallbackWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackWidth));
        }

        if (fallbackHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackHeight));
        }

        return HasPositiveArea(windowBounds)
            ? windowBounds
            : windowBounds with
            {
                Width = fallbackWidth,
                Height = fallbackHeight,
            };
    }

    public static DesktopRectangle ToDesktopCoordinates(
        DesktopRectangle displayBounds,
        DesktopRectangle displayRelativeBounds) =>
        new(
            ClampToInt((long)displayBounds.X + displayRelativeBounds.X),
            ClampToInt((long)displayBounds.Y + displayRelativeBounds.Y),
            displayRelativeBounds.Width,
            displayRelativeBounds.Height);

    public static bool IntersectsAnyWorkArea(
        DesktopRectangle windowBounds,
        IEnumerable<DesktopRectangle> workAreas)
    {
        ArgumentNullException.ThrowIfNull(workAreas);

        if (!HasPositiveArea(windowBounds))
        {
            return false;
        }

        return workAreas.Any(workArea => Intersects(windowBounds, workArea));
    }

    public static DesktopRectangle CenterInWorkArea(
        DesktopRectangle windowBounds,
        DesktopRectangle workArea)
    {
        if (!HasPositiveArea(windowBounds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowBounds),
                "The window must have a positive size.");
        }

        if (!HasPositiveArea(workArea))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workArea),
                "The display work area must have a positive size.");
        }

        var xOffset = Math.Max(0L, ((long)workArea.Width - windowBounds.Width) / 2);
        var yOffset = Math.Max(0L, ((long)workArea.Height - windowBounds.Height) / 2);

        return new DesktopRectangle(
            ClampToInt((long)workArea.X + xOffset),
            ClampToInt((long)workArea.Y + yOffset),
            windowBounds.Width,
            windowBounds.Height);
    }

    private static bool Intersects(DesktopRectangle first, DesktopRectangle second)
    {
        if (!HasPositiveArea(second))
        {
            return false;
        }

        return (long)first.X < (long)second.X + second.Width &&
            (long)first.X + first.Width > second.X &&
            (long)first.Y < (long)second.Y + second.Height &&
            (long)first.Y + first.Height > second.Y;
    }

    private static bool HasPositiveArea(DesktopRectangle rectangle) =>
        rectangle.Width > 0 && rectangle.Height > 0;

    private static int ClampToInt(long value) =>
        (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}
