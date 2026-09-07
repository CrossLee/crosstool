using System.Drawing;

namespace Crosio.Windows.Capture.Pinning;

/// <summary>
/// Pure, DPI-aware geometry shared by the pinned screenshot window and unit
/// tests. Coordinates are virtual-desktop physical pixels.
/// </summary>
public static class PinnedScreenshotLayout
{
    public const float MaximumInitialWidthDip = 520;
    public const float MaximumInitialHeightDip = 420;
    public const float MaximumInitialWorkingAreaFraction = 0.40f;
    public const float MaximumZoomWorkingAreaFraction = 0.90f;
    public const float MinimumLongestSideDip = 120;
    public const float AnchorGapDip = 14;

    public static RectangleF InitialBounds(
        SizeF sourcePixelSize,
        float dpiScale,
        RectangleF workingArea,
        PointF anchor)
    {
        workingArea = UsableRectangle(workingArea);
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
        {
            return new RectangleF(anchor, SizeF.Empty);
        }

        dpiScale = FinitePositive(dpiScale, fallback: 1);
        var source = UsableSize(sourcePixelSize);
        var maximumWidth = Math.Max(
            1,
            Math.Min(MaximumInitialWidthDip * dpiScale, workingArea.Width * MaximumInitialWorkingAreaFraction));
        var maximumHeight = Math.Max(
            1,
            Math.Min(MaximumInitialHeightDip * dpiScale, workingArea.Height * MaximumInitialWorkingAreaFraction));
        var scale = Math.Min(
            1,
            Math.Min(maximumWidth / source.Width, maximumHeight / source.Height));
        var size = new SizeF(source.Width * scale, source.Height * scale);

        var longestSide = Math.Max(size.Width, size.Height);
        var attainableMinimum = Math.Min(
            MinimumLongestSideDip * dpiScale,
            Math.Max(maximumWidth, maximumHeight));
        if (longestSide > 0 && longestSide < attainableMinimum)
        {
            scale = Math.Min(
                attainableMinimum / longestSide,
                Math.Min(maximumWidth / size.Width, maximumHeight / size.Height));
            size = new SizeF(size.Width * scale, size.Height * scale);
        }

        var gap = AnchorGapDip * dpiScale;
        var candidates = new[]
        {
            new PointF(anchor.X + gap, anchor.Y + gap),
            new PointF(anchor.X - size.Width - gap, anchor.Y + gap),
            new PointF(anchor.X + gap, anchor.Y - size.Height - gap),
            new PointF(anchor.X - size.Width - gap, anchor.Y - size.Height - gap),
        };

        RectangleF? best = null;
        var smallestAdjustmentSquared = float.PositiveInfinity;
        foreach (var origin in candidates)
        {
            var proposed = new RectangleF(origin, size);
            var adjusted = Clamp(proposed, workingArea);
            var deltaX = adjusted.Left - proposed.Left;
            var deltaY = adjusted.Top - proposed.Top;
            var adjustmentSquared = (deltaX * deltaX) + (deltaY * deltaY);
            if (adjustmentSquared < smallestAdjustmentSquared)
            {
                best = adjusted;
                smallestAdjustmentSquared = adjustmentSquared;
            }
        }

        return best ?? Clamp(new RectangleF(anchor, size), workingArea);
    }

    public static RectangleF Clamp(RectangleF bounds, RectangleF workingArea)
    {
        workingArea = UsableRectangle(workingArea);
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
        {
            return RectangleF.Empty;
        }

        bounds = Standardized(bounds);
        var size = UsableSize(bounds.Size);
        var scale = Math.Min(
            1,
            Math.Min(workingArea.Width / size.Width, workingArea.Height / size.Height));
        size = new SizeF(size.Width * scale, size.Height * scale);
        var proposedX = float.IsFinite(bounds.Left) ? bounds.Left : workingArea.Left;
        var proposedY = float.IsFinite(bounds.Top) ? bounds.Top : workingArea.Top;
        var x = Math.Min(
            Math.Max(proposedX, workingArea.Left),
            workingArea.Right - size.Width);
        var y = Math.Min(
            Math.Max(proposedY, workingArea.Top),
            workingArea.Bottom - size.Height);
        return new RectangleF(x, y, size.Width, size.Height);
    }

    public static SizeF MinimumSize(SizeF sourcePixelSize, float dpiScale)
    {
        var source = UsableSize(sourcePixelSize);
        dpiScale = FinitePositive(dpiScale, fallback: 1);
        var longestSide = MinimumLongestSideDip * dpiScale;
        return source.Width >= source.Height
            ? new SizeF(longestSide, longestSide * source.Height / source.Width)
            : new SizeF(longestSide * source.Width / source.Height, longestSide);
    }

    public static SizeF MaximumSize(RectangleF workingArea)
    {
        workingArea = UsableRectangle(workingArea);
        return new SizeF(
            Math.Max(1, workingArea.Width * MaximumZoomWorkingAreaFraction),
            Math.Max(1, workingArea.Height * MaximumZoomWorkingAreaFraction));
    }

    public static RectangleF ZoomedBounds(
        RectangleF bounds,
        PointF screenPoint,
        float scaleFactor,
        SizeF minimumSize,
        SizeF maximumSize,
        RectangleF workingArea)
    {
        workingArea = UsableRectangle(workingArea);
        bounds = Standardized(bounds);
        if (workingArea.Width <= 0
            || workingArea.Height <= 0
            || bounds.Width <= 0
            || bounds.Height <= 0
            || !float.IsFinite(scaleFactor)
            || scaleFactor <= 0)
        {
            return Clamp(bounds, workingArea);
        }

        minimumSize = PositiveSize(minimumSize);
        maximumSize = PositiveSize(maximumSize);
        var maximumWidth = Math.Min(maximumSize.Width, workingArea.Width);
        var maximumHeight = Math.Min(maximumSize.Height, workingArea.Height);
        if (maximumWidth <= 0 || maximumHeight <= 0)
        {
            return Clamp(bounds, workingArea);
        }

        var minimumScale = Math.Max(
            minimumSize.Width / bounds.Width,
            minimumSize.Height / bounds.Height);
        var maximumScale = Math.Min(
            maximumWidth / bounds.Width,
            maximumHeight / bounds.Height);
        var appliedScale = minimumScale <= maximumScale
            ? Math.Min(Math.Max(scaleFactor, minimumScale), maximumScale)
            : maximumScale;
        var anchor = new PointF(
            float.IsFinite(screenPoint.X) ? screenPoint.X : bounds.Left + (bounds.Width / 2),
            float.IsFinite(screenPoint.Y) ? screenPoint.Y : bounds.Top + (bounds.Height / 2));
        var unitX = Math.Clamp((anchor.X - bounds.Left) / bounds.Width, 0, 1);
        var unitY = Math.Clamp((anchor.Y - bounds.Top) / bounds.Height, 0, 1);
        var size = new SizeF(bounds.Width * appliedScale, bounds.Height * appliedScale);
        var proposed = new RectangleF(
            anchor.X - (unitX * size.Width),
            anchor.Y - (unitY * size.Height),
            size.Width,
            size.Height);
        return Clamp(proposed, workingArea);
    }

    public static float? ScaleFactorForWheel(
        float deltaX,
        float deltaY,
        bool hasPreciseDeltas,
        bool hasMomentum)
    {
        if (hasMomentum
            || !float.IsFinite(deltaX)
            || !float.IsFinite(deltaY)
            || Math.Abs(deltaY) <= Math.Abs(deltaX)
            || deltaY == 0)
        {
            return null;
        }

        var exponent = hasPreciseDeltas
            ? deltaY * 0.01
            : (deltaY / 120.0) * Math.Log(1.10);
        exponent = Math.Clamp(exponent, Math.Log(0.5), Math.Log(2));
        return (float)Math.Exp(exponent);
    }

    private static RectangleF Standardized(RectangleF rectangle)
    {
        var left = Math.Min(rectangle.Left, rectangle.Right);
        var top = Math.Min(rectangle.Top, rectangle.Bottom);
        return new RectangleF(left, top, Math.Abs(rectangle.Width), Math.Abs(rectangle.Height));
    }

    private static RectangleF UsableRectangle(RectangleF rectangle)
    {
        return float.IsFinite(rectangle.Left)
            && float.IsFinite(rectangle.Top)
            && float.IsFinite(rectangle.Width)
            && float.IsFinite(rectangle.Height)
            ? Standardized(rectangle)
            : RectangleF.Empty;
    }

    private static SizeF UsableSize(SizeF size) => new(
        Math.Max(1, FinitePositive(size.Width, fallback: 1)),
        Math.Max(1, FinitePositive(size.Height, fallback: 1)));

    private static SizeF PositiveSize(SizeF size) => new(
        FinitePositive(size.Width, fallback: 0),
        FinitePositive(size.Height, fallback: 0));

    private static float FinitePositive(float value, float fallback) =>
        float.IsFinite(value) && value > 0 ? value : fallback;
}
