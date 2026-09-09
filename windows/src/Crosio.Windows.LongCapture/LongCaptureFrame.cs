namespace Crosio.Windows.LongCapture;

/// <summary>
/// An immutable, tightly packed RGBA frame. Keeping the stitching core free of
/// Windows imaging objects makes overlap and scheduling tests runnable on any
/// development host.
/// </summary>
public sealed class LongCaptureFrame
{
    public const int BytesPerPixel = 4;

    private readonly byte[] _pixels;

    public LongCaptureFrame(int width, int height, ReadOnlySpan<byte> rgbaPixels)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var expectedLength = checked(width * height * BytesPerPixel);
        if (rgbaPixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"The frame requires exactly {expectedLength:N0} RGBA bytes.",
                nameof(rgbaPixels));
        }

        Width = width;
        Height = height;
        _pixels = rgbaPixels.ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    public long PixelCount => checked((long)Width * Height);

    public ReadOnlyMemory<byte> Pixels => _pixels;

    public byte[] CopyPixels() => (byte[])_pixels.Clone();

    internal ReadOnlySpan<byte> PixelSpan => _pixels;
}
