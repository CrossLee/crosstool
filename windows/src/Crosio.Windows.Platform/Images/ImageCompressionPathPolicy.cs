using Crosio.Windows.Platform.IO;

namespace Crosio.Windows.Platform.Images;

public enum PreservedImageFormat
{
    Jpeg,
    Png,
    Heif,
    Tiff,
}

public static class ImageCompressionPathPolicy
{
    private static readonly IReadOnlyDictionary<string, PreservedImageFormat> SupportedExtensions =
        new Dictionary<string, PreservedImageFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = PreservedImageFormat.Jpeg,
            [".jpeg"] = PreservedImageFormat.Jpeg,
            [".jpe"] = PreservedImageFormat.Jpeg,
            [".jfif"] = PreservedImageFormat.Jpeg,
            [".png"] = PreservedImageFormat.Png,
            [".heic"] = PreservedImageFormat.Heif,
            [".heif"] = PreservedImageFormat.Heif,
            [".tif"] = PreservedImageFormat.Tiff,
            [".tiff"] = PreservedImageFormat.Tiff,
        };

    public static bool IsSupportedImagePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        SupportedExtensions.ContainsKey(WindowsPathPolicy.GetExtension(path));

    public static PreservedImageFormat GetRequiredFormat(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!SupportedExtensions.TryGetValue(WindowsPathPolicy.GetExtension(sourcePath), out var format))
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.SourceFormatCannotBePreserved,
                $"Crosio cannot compress {WindowsPathPolicy.GetExtension(sourcePath)} without changing its format.");
        }

        return format;
    }

    public static string CreateCandidatePath(string sourcePath, int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!WindowsPathPolicy.IsFullyQualified(sourcePath))
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.InvalidPath,
                "Image compression requires a fully qualified local or network path.");
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        string directoryPrefix;
        string stem;
        string extension;
        try
        {
            (directoryPrefix, stem, extension) = WindowsPathPolicy.SplitImagePath(sourcePath);
        }
        catch (ArgumentException exception)
        {
            throw new ImageCompressionException(
                ImageCompressionErrorCode.InvalidPath,
                "The source image path has no usable directory, name, or extension.",
                exception);
        }

        // Path.GetExtension preserves the source spelling (for example .JPEG),
        // which is a user-visible part of the preserve-format contract.
        var suffix = sequence == 1 ? "-crosio" : $"-crosio-{sequence}";
        return $"{directoryPrefix}{stem}{suffix}{extension}";
    }
}
