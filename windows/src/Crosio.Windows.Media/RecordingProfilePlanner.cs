namespace Crosio.Windows.Media;

public sealed record RecordingSafetyLimits(
    TimeSpan MaximumDuration,
    long MaximumFileBytes,
    long RequiredFreeBytesBeforeStart,
    long ReservedFreeBytesWhileRecording,
    int MaximumLongestEdge,
    long MaximumPixelCount)
{
    /// <summary>
    /// Bounds the wait for the native recorder to report that its MP4
    /// container has been finalized. A recorder callback must never be able to
    /// keep Crosio in the stopping state forever.
    /// </summary>
    public TimeSpan StopFinalizationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static RecordingSafetyLimits Default { get; } = new(
        TimeSpan.FromHours(2),
        10L * 1024 * 1024 * 1024,
        2L * 1024 * 1024 * 1024,
        1L * 1024 * 1024 * 1024,
        3840,
        8_300_000);
}

public sealed record RecordingEncodingProfile(
    PixelSize OutputSize,
    uint FrameRateNumerator,
    uint FrameRateDenominator,
    uint VideoBitrate,
    string VideoCodec,
    string? AudioCodec,
    uint AudioSampleRate,
    uint AudioChannels,
    uint AudioBitrate,
    string Container,
    string FileExtension);

public static class RecordingProfilePlanner
{
    public const uint TargetFrameRate = 30;

    public static RecordingEncodingProfile Create(
        PixelSize sourceSize,
        bool includeSystemAudio,
        RecordingSafetyLimits? limits = null)
    {
        limits ??= RecordingSafetyLimits.Default;
        ValidateLimits(limits);

        var outputSize = ScaleToLimits(sourceSize, limits.MaximumLongestEdge, limits.MaximumPixelCount);
        var pixels = outputSize.PixelCount;
        var videoBitrate = (uint)Math.Clamp(pixels * 2L, 4_000_000L, 24_000_000L);

        return new RecordingEncodingProfile(
            outputSize,
            TargetFrameRate,
            1,
            videoBitrate,
            "H264",
            includeSystemAudio ? "AAC" : null,
            includeSystemAudio ? 48_000U : 0,
            includeSystemAudio ? 2U : 0,
            includeSystemAudio ? 192_000U : 0,
            "MP4",
            ".mp4");
    }

    public static PixelSize ScaleToLimits(
        PixelSize sourceSize,
        int maximumLongestEdge,
        long maximumPixelCount)
    {
        if (maximumLongestEdge <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLongestEdge));
        }

        if (maximumPixelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPixelCount));
        }

        var edgeScale = Math.Min(1D, maximumLongestEdge / (double)Math.Max(sourceSize.Width, sourceSize.Height));
        var pixelScale = Math.Min(1D, Math.Sqrt(maximumPixelCount / (double)sourceSize.PixelCount));
        var scale = Math.Min(edgeScale, pixelScale);

        var width = MakeEvenAtLeastTwo((int)Math.Floor(sourceSize.Width * scale));
        var height = MakeEvenAtLeastTwo((int)Math.Floor(sourceSize.Height * scale));

        // Rounding to an even encoder dimension can only lower dimensions, but
        // guard the invariants explicitly so changes fail closed.
        while (Math.Max(width, height) > maximumLongestEdge || checked((long)width * height) > maximumPixelCount)
        {
            if (width >= height && width > 2)
            {
                width -= 2;
            }
            else if (height > 2)
            {
                height -= 2;
            }
            else
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceSize),
                    "The configured recording limits cannot fit a valid H.264 frame.");
            }
        }

        return new PixelSize(width, height);
    }

    private static int MakeEvenAtLeastTwo(int value) => Math.Max(2, value - (value & 1));

    private static void ValidateLimits(RecordingSafetyLimits limits)
    {
        if (limits.MaximumDuration <= TimeSpan.Zero ||
            limits.MaximumFileBytes <= 0 ||
            limits.RequiredFreeBytesBeforeStart <= 0 ||
            limits.ReservedFreeBytesWhileRecording <= 0 ||
            limits.MaximumLongestEdge < 2 ||
            limits.MaximumPixelCount < 4 ||
            limits.StopFinalizationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Recording safety limits must be positive.");
        }
    }
}
