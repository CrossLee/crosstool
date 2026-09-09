#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Crosio.Windows.Capture;

namespace Crosio.Windows.LongCapture;

internal static class WindowsLongCaptureFrameCodec
{
    internal static LongCaptureFrame Decode(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var source = new MemoryStream(image.CopyPngBytes(), writable: false);
        using var decoded = new Bitmap(source);
        using var rgba = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(rgba))
        {
            graphics.DrawImageUnscaled(decoded, 0, 0);
        }

        var bounds = new Rectangle(0, 0, rgba.Width, rgba.Height);
        var data = rgba.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = GC.AllocateUninitializedArray<byte>(
                checked(rgba.Width * rgba.Height * LongCaptureFrame.BytesPerPixel));
            var sourceRow = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < rgba.Height; y++)
            {
                var physicalY = data.Stride >= 0 ? y : rgba.Height - 1 - y;
                var rowAddress = data.Scan0 + (physicalY * Math.Abs(data.Stride));
                Marshal.Copy(rowAddress, sourceRow, 0, sourceRow.Length);
                var destinationRow = checked(y * rgba.Width * LongCaptureFrame.BytesPerPixel);
                for (var x = 0; x < rgba.Width; x++)
                {
                    var sourceOffset = x * LongCaptureFrame.BytesPerPixel;
                    var destinationOffset = destinationRow + sourceOffset;
                    pixels[destinationOffset] = sourceRow[sourceOffset + 2];
                    pixels[destinationOffset + 1] = sourceRow[sourceOffset + 1];
                    pixels[destinationOffset + 2] = sourceRow[sourceOffset];
                    pixels[destinationOffset + 3] = sourceRow[sourceOffset + 3];
                }
            }

            return new LongCaptureFrame(rgba.Width, rgba.Height, pixels);
        }
        finally
        {
            rgba.UnlockBits(data);
        }
    }

    internal static CapturedImage Encode(LongCaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = frame.PixelSpan;
            var destinationRow = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < frame.Height; y++)
            {
                Array.Clear(destinationRow);
                var sourceRow = checked(y * frame.Width * LongCaptureFrame.BytesPerPixel);
                for (var x = 0; x < frame.Width; x++)
                {
                    var sourceOffset = sourceRow + (x * LongCaptureFrame.BytesPerPixel);
                    var destinationOffset = x * LongCaptureFrame.BytesPerPixel;
                    destinationRow[destinationOffset] = pixels[sourceOffset + 2];
                    destinationRow[destinationOffset + 1] = pixels[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = pixels[sourceOffset];
                    destinationRow[destinationOffset + 3] = pixels[sourceOffset + 3];
                }

                var physicalY = data.Stride >= 0 ? y : frame.Height - 1 - y;
                var rowAddress = data.Scan0 + (physicalY * Math.Abs(data.Stride));
                Marshal.Copy(destinationRow, 0, rowAddress, destinationRow.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return new CapturedImage(
            output.GetBuffer().AsSpan(0, checked((int)output.Length)),
            frame.Width,
            frame.Height);
    }
}
#endif
