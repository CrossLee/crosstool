namespace Crosio.Windows.Platform.Images;

public sealed record ImageCompressionSettings(
    long TargetBytes = 500_000,
    uint? MaximumDimension = null,
    bool AllowTargetDrivenResize = false)
{
    public const long MaximumTargetBytes = 512L * 1024L * 1024L;

    public void Validate()
    {
        if (TargetBytes <= 0 || TargetBytes > MaximumTargetBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TargetBytes),
                $"TargetBytes must be between 1 and {MaximumTargetBytes}.");
        }

        if (MaximumDimension is 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDimension),
                "MaximumDimension must be greater than zero when supplied.");
        }
    }
}

public enum ImageCompressionDisposition
{
    Compressed,
    AlreadyOptimized,
}

public sealed record ImageCompressionResult(
    string SourcePath,
    string OutputPath,
    long OriginalBytes,
    long CompressedBytes,
    uint PixelWidth,
    uint PixelHeight,
    bool MetTargetSize)
{
    public long SavedBytes => Math.Max(0, OriginalBytes - CompressedBytes);

    public int ReductionPercentage => OriginalBytes <= 0
        ? 0
        : Math.Clamp(
            (int)Math.Round(
                100d * (1d - ((double)CompressedBytes / OriginalBytes)),
                MidpointRounding.AwayFromZero),
            0,
            100);
}

public sealed record ImageCompressionOutcome(
    ImageCompressionDisposition Disposition,
    long OriginalBytes,
    ImageCompressionResult? Result)
{
    public static ImageCompressionOutcome Compressed(ImageCompressionResult result) =>
        new(ImageCompressionDisposition.Compressed, result.OriginalBytes, result);

    public static ImageCompressionOutcome AlreadyOptimized(long originalBytes) =>
        new(ImageCompressionDisposition.AlreadyOptimized, originalBytes, null);
}

public enum ImageCompressionErrorCode
{
    InvalidPath,
    FileNotFound,
    UnsupportedImage,
    SourceFormatCannotBePreserved,
    AnimatedOrMultipageImage,
    InvalidDimensions,
    ImageTooLarge,
    DecoderUnavailable,
    EncoderUnavailable,
    EncodingFailed,
    CannotWriteOutput,
}

public sealed class ImageCompressionException : Exception
{
    public ImageCompressionException(
        ImageCompressionErrorCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ImageCompressionErrorCode Code { get; }
}

public interface IImageCompressionService
{
    Task<ImageCompressionOutcome> CompressAsync(
        string sourcePath,
        ImageCompressionSettings settings,
        CancellationToken cancellationToken = default);
}
