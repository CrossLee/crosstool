using global::Windows.Foundation;
using global::Windows.Graphics.Imaging;
using global::Windows.Storage;
using global::Windows.Storage.Streams;
using Crosio.Windows.Platform.IO;
using System.Runtime.Versioning;

namespace Crosio.Windows.Platform.Images;

/// <summary>
/// On-device image compression backed by the Windows Imaging Component codecs
/// exposed through Windows.Graphics.Imaging. The source format and source file
/// extension are always preserved; source files are never overwritten.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ImageCompressionService : IImageCompressionService
{
    public const uint MaximumPixelDimension = 32_768;
    public const ulong MaximumPixelCount = 50_000_000;
    public const uint MinimumAutomaticPixelDimension = 960;
    public const long MinimumMeaningfulSavings = 16_384;

    private static readonly float[] JpegQualityProbes =
    [
        0.95f,
        0.90f,
        0.85f,
        0.80f,
        0.75f,
        0.70f,
    ];

    public async Task<ImageCompressionOutcome> CompressAsync(
        string sourcePath,
        ImageCompressionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourcePath) ||
            sourcePath.IndexOf('\0') >= 0 ||
            !WindowsPathPolicy.IsFullyQualified(sourcePath))
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.InvalidPath,
                "Image compression requires a fully qualified file path.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.FileNotFound,
                "The selected image no longer exists.");
        }

        var requiredFormat = ImageCompressionPathPolicy.GetRequiredFormat(sourcePath);
        var codec = ImageCodec.For(requiredFormat);
        var originalBytes = new FileInfo(sourcePath).Length;

        StorageFile sourceFile;
        try
        {
            sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
        }
        catch (Exception exception)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.UnsupportedImage,
                "Windows could not open the selected image.",
                exception);
        }

        using var sourceStream = await sourceFile.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(sourceStream);
        }
        catch (Exception exception)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.DecoderUnavailable,
                "Windows could not decode this image. The required image codec may be missing.",
                exception);
        }

        if (decoder.FrameCount != 1)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.AnimatedOrMultipageImage,
                "一爪 does not compress animated or multipage images because frames could be lost.");
        }

        if (decoder.DecoderInformation.CodecId != codec.DecoderId)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.SourceFormatCannotBePreserved,
                "The file contents do not match its extension, so 一爪 cannot preserve the format safely.");
        }

        var sourceWidth = decoder.OrientedPixelWidth;
        var sourceHeight = decoder.OrientedPixelHeight;
        ValidateDimensions(sourceWidth, sourceHeight);

        var sourceLongestEdge = Math.Max(sourceWidth, sourceHeight);
        var initialLimit = Math.Min(settings.MaximumDimension ?? sourceLongestEdge, sourceLongestEdge);
        if (initialLimit == 0)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.InvalidDimensions,
                "The image has invalid pixel dimensions.");
        }

        var requiresDimensionChange = initialLimit < sourceLongestEdge;
        if (originalBytes <= settings.TargetBytes && !requiresDimensionChange)
        {
            return ImageCompressionOutcome.AlreadyOptimized(originalBytes);
        }

        EncodedCandidate candidate;
        try
        {
            candidate = await FindBestCandidateAsync(
                decoder,
                codec,
                sourceWidth,
                sourceHeight,
                initialLimit,
                settings,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImageCompressionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.EncodingFailed,
                "Windows could not encode a compressed copy of this image.",
                exception);
        }

        var savedBytes = originalBytes - candidate.Bytes.LongLength;
        var meaningfulSavings = Math.Max(MinimumMeaningfulSavings, originalBytes / 20);
        var metTarget = candidate.Bytes.LongLength <= settings.TargetBytes;
        if (savedBytes <= 0 ||
            (!metTarget && !requiresDimensionChange && savedBytes < meaningfulSavings))
        {
            return ImageCompressionOutcome.AlreadyOptimized(originalBytes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var outputPath = await WriteWithoutOverwritingAsync(
            sourcePath,
            candidate.Bytes,
            cancellationToken);

        return ImageCompressionOutcome.Compressed(new ImageCompressionResult(
            sourcePath,
            outputPath,
            originalBytes,
            candidate.Bytes.LongLength,
            candidate.Width,
            candidate.Height,
            metTarget));
    }

    private static void ValidateDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.InvalidDimensions,
                "The image has invalid pixel dimensions.");
        }

        if (width > MaximumPixelDimension ||
            height > MaximumPixelDimension ||
            (ulong)width * height > MaximumPixelCount)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.ImageTooLarge,
                "一爪 does not process images larger than 50 million pixels or 32768 pixels on one edge.");
        }
    }

    private static async Task<EncodedCandidate> FindBestCandidateAsync(
        BitmapDecoder decoder,
        ImageCodec codec,
        uint sourceWidth,
        uint sourceHeight,
        uint initialLimit,
        ImageCompressionSettings settings,
        CancellationToken cancellationToken)
    {
        EncodedCandidate? best = null;
        var currentLimit = initialLimit;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (width, height) = CalculateDimensions(sourceWidth, sourceHeight, currentLimit);
            IEnumerable<float?> qualities = codec.SupportsImageQuality
                ? JpegQualityProbes.Select(static quality => (float?)quality)
                : new float?[] { null };

            foreach (var quality in qualities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var encoded = await EncodeAsync(
                    decoder,
                    codec,
                    width,
                    height,
                    quality,
                    cancellationToken);
                if (best is null || encoded.Bytes.Length < best.Bytes.Length)
                {
                    best = encoded;
                }

                if (encoded.Bytes.LongLength <= settings.TargetBytes)
                {
                    return encoded;
                }
            }

            if (!settings.AllowTargetDrivenResize || best is null)
            {
                break;
            }

            var minimumLimit = Math.Min(
                MinimumAutomaticPixelDimension,
                Math.Max(sourceWidth, sourceHeight));
            var nextLimit = NextPixelLimit(
                currentLimit,
                minimumLimit,
                settings.TargetBytes,
                best.Bytes.LongLength);
            if (nextLimit is null)
            {
                break;
            }

            currentLimit = nextLimit.Value;
        }

        return best ?? throw new ImageCompressionException(
            ImageCompressionErrorCode.EncodingFailed,
            "Windows did not produce a compressed image candidate.");
    }

    internal static uint? NextPixelLimit(
        uint current,
        uint minimum,
        long targetBytes,
        long currentBytes)
    {
        if (current <= minimum || targetBytes <= 0 || currentBytes <= targetBytes)
        {
            return null;
        }

        var estimatedScale = Math.Sqrt((double)targetBytes / currentBytes) * 0.94d;
        var boundedScale = Math.Clamp(estimatedScale, 0.72d, 0.90d);
        var proposed = Math.Min(current - 1, (uint)Math.Floor(current * boundedScale));
        return proposed < minimum ? minimum : proposed;
    }

    internal static (uint Width, uint Height) CalculateDimensions(
        uint sourceWidth,
        uint sourceHeight,
        uint maximumDimension)
    {
        var longest = Math.Max(sourceWidth, sourceHeight);
        if (longest <= maximumDimension)
        {
            return (sourceWidth, sourceHeight);
        }

        var scale = (double)maximumDimension / longest;
        var width = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));
        return (width, height);
    }

    private static async Task<EncodedCandidate> EncodeAsync(
        BitmapDecoder decoder,
        ImageCodec codec,
        uint width,
        uint height,
        float? quality,
        CancellationToken cancellationToken)
    {
        var byteCount = checked((ulong)width * height * 4UL);
        if (byteCount > int.MaxValue)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.ImageTooLarge,
                "The decoded image would exceed 一爪's in-memory safety limit.");
        }

        var alphaMode = codec.PreservesAlpha
            ? BitmapAlphaMode.Straight
            : BitmapAlphaMode.Ignore;
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        PixelDataProvider pixels;
        try
        {
            pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                alphaMode,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
        }
        catch (Exception exception)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.UnsupportedImage,
                "Windows could not decode the image pixels.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var detachedPixels = pixels.DetachPixelData();
        if ((ulong)detachedPixels.LongLength != byteCount)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.EncodingFailed,
                "The decoded image buffer has an unexpected size.");
        }

        var properties = new BitmapPropertySet();
        if (quality is not null)
        {
            properties.Add(
                "ImageQuality",
                new BitmapTypedValue(quality.Value, PropertyType.Single));
        }

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        try
        {
            encoder = properties.Count == 0
                ? await BitmapEncoder.CreateAsync(codec.EncoderId, output)
                : await BitmapEncoder.CreateAsync(codec.EncoderId, output, properties);
        }
        catch (Exception exception)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.EncoderUnavailable,
                $"Windows cannot write {codec.DisplayName} images. Install the matching Windows image codec and try again.",
                exception);
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            alphaMode,
            width,
            height,
            96,
            96,
            detachedPixels);

        try
        {
            await encoder.FlushAsync();
        }
        catch (Exception exception)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.EncodingFailed,
                $"Windows failed while writing the {codec.DisplayName} image.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        output.Seek(0);
        var validationDecoder = await BitmapDecoder.CreateAsync(output);
        if (validationDecoder.DecoderInformation.CodecId != codec.DecoderId ||
            validationDecoder.FrameCount != 1 ||
            validationDecoder.OrientedPixelWidth != width ||
            validationDecoder.OrientedPixelHeight != height)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.EncodingFailed,
                "The encoded image failed 一爪's format or dimension validation.");
        }

        output.Seek(0);
        var bytes = await ReadAllBytesAsync(output, cancellationToken);
        return new EncodedCandidate(bytes, width, height);
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        IRandomAccessStream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Size == 0 || stream.Size > int.MaxValue)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.EncodingFailed,
                "The encoded image has an invalid size.");
        }

        var length = checked((uint)stream.Size);
        using var input = stream.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        var loaded = await reader.LoadAsync(length);
        cancellationToken.ThrowIfCancellationRequested();
        if (loaded != length)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.EncodingFailed,
                "Windows returned an incomplete encoded image.");
        }

        var bytes = new byte[checked((int)length)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    internal static async Task<string> WriteWithoutOverwritingAsync(
        string sourcePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new ImageCompressionException(
                ImageCompressionErrorCode.InvalidPath,
                "The source image has no parent folder.");
        var temporaryPath = Path.Combine(
            directory,
            $".crosio-compression-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                options: FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            for (var sequence = 1; sequence < 100_000; sequence++)
            {
                var candidate = ImageCompressionPathPolicy.CreateCandidatePath(sourcePath, sequence);
                try
                {
                    File.Move(temporaryPath, candidate, overwrite: false);
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // A concurrent compression won this filename. Keep the fully
                    // written temp file and atomically try the next suffix.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImageCompressionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.CannotWriteOutput,
                "一爪 could not write the compressed copy beside the source image.",
                exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // The final move already succeeded, or cleanup will be retried by
                // the operating system/user. Never delete a source or final file.
            }
        }

        throw new ImageCompressionException(
            ImageCompressionErrorCode.CannotWriteOutput,
            "一爪 could not allocate a unique output filename.");
    }

    private sealed record EncodedCandidate(byte[] Bytes, uint Width, uint Height);

    private sealed record ImageCodec(
        Guid DecoderId,
        Guid EncoderId,
        string DisplayName,
        bool SupportsImageQuality,
        bool PreservesAlpha)
    {
        public static ImageCodec For(PreservedImageFormat format) => format switch
        {
            PreservedImageFormat.Jpeg => new(
                BitmapDecoder.JpegDecoderId,
                BitmapEncoder.JpegEncoderId,
                "JPEG",
                SupportsImageQuality: true,
                PreservesAlpha: false),
            PreservedImageFormat.Png => new(
                BitmapDecoder.PngDecoderId,
                BitmapEncoder.PngEncoderId,
                "PNG",
                SupportsImageQuality: false,
                PreservesAlpha: true),
            PreservedImageFormat.Heif => new(
                BitmapDecoder.HeifDecoderId,
                BitmapEncoder.HeifEncoderId,
                "HEIF",
                SupportsImageQuality: false,
                PreservesAlpha: false),
            PreservedImageFormat.Tiff => new(
                BitmapDecoder.TiffDecoderId,
                BitmapEncoder.TiffEncoderId,
                "TIFF",
                SupportsImageQuality: false,
                PreservesAlpha: true),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }
}
