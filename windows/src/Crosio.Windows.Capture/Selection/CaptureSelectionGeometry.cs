using System.Drawing;

namespace Crosio.Windows.Capture.Selection;

public static class CaptureSelectionGeometry
{
    public static PixelRect? RegionFromEndpoints(
        Point firstScreenPoint,
        Point secondScreenPoint,
        int minimumSideLength = 2)
    {
        if (minimumSideLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSideLength));
        }

        var left = Math.Min(firstScreenPoint.X, secondScreenPoint.X);
        var top = Math.Min(firstScreenPoint.Y, secondScreenPoint.Y);
        var width = checked(Math.Abs(secondScreenPoint.X - firstScreenPoint.X));
        var height = checked(Math.Abs(secondScreenPoint.Y - firstScreenPoint.Y));
        return width >= minimumSideLength && height >= minimumSideLength
            ? new PixelRect(left, top, width, height)
            : null;
    }
}
