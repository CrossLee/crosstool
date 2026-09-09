namespace Crosio.Windows.Media;

/// <summary>
/// A point in virtual-desktop physical pixels. Coordinates may be negative
/// when a monitor is positioned above or to the left of the primary monitor.
/// </summary>
public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelSize
{
    public PixelSize(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public long PixelCount => checked((long)Width * Height);
}

public readonly record struct PixelRectangle
{
    public PixelRectangle(int left, int top, int width, int height)
    {
        Size = new PixelSize(width, height);
        Left = left;
        Top = top;
    }

    public int Left { get; }

    public int Top { get; }

    public PixelSize Size { get; }

    public int Width => Size.Width;

    public int Height => Size.Height;

    public int Right => checked(Left + Width);

    public int Bottom => checked(Top + Height);
}
