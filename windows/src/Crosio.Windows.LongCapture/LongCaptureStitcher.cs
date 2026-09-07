namespace Crosio.Windows.LongCapture;

public enum LongCaptureAddFrameStatus
{
    Initialized,
    Added,
    NoMovement,
    AmbiguousOverlap,
    NoOverlap,
    IncompatibleFrame,
    LimitExceeded,
}

public sealed record LongCaptureAddFrameResult(
    LongCaptureAddFrameStatus Status,
    int OutputWidth,
    int OutputHeight,
    int AddedPixels,
    VerticalOverlapMatch? Match = null)
{
    public bool Accepted => Status is LongCaptureAddFrameStatus.Initialized
        or LongCaptureAddFrameStatus.Added
        or LongCaptureAddFrameStatus.NoMovement;
}

/// <summary>
/// Maintains a content-coordinate canvas and the latest viewport position, so
/// downwards appends, upwards prepends, and direction reversals do not duplicate
/// already captured pixels.
/// </summary>
public sealed class LongCaptureStitcher
{
    public const long DefaultMaximumPixelCount = 20_000_000;
    public const int DefaultMaximumEdgePixels = 20_000;

    private readonly VerticalOverlapMatcher _matcher;
    private readonly long _maximumPixelCount;
    private readonly int _maximumEdgePixels;
    private LongCaptureFrame? _currentFrame;
    private byte[]? _canvas;
    private int _contentTop;
    private int _contentBottom;
    private int _currentOffset;

    public LongCaptureStitcher(
        VerticalOverlapMatcher? matcher = null,
        long maximumPixelCount = DefaultMaximumPixelCount,
        int maximumEdgePixels = DefaultMaximumEdgePixels)
    {
        if (maximumPixelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPixelCount));
        }

        if (maximumEdgePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdgePixels));
        }

        _matcher = matcher ?? new VerticalOverlapMatcher();
        _maximumPixelCount = maximumPixelCount;
        _maximumEdgePixels = maximumEdgePixels;
    }

    public bool HasFrame => _currentFrame is not null;

    public int OutputWidth => _currentFrame?.Width ?? 0;

    public int OutputHeight => _canvas is null ? 0 : checked(_contentBottom - _contentTop);

    public long OutputPixelCount => checked((long)OutputWidth * OutputHeight);

    public LongCaptureAddFrameResult AddInitialFrame(LongCaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_currentFrame is not null)
        {
            throw new InvalidOperationException("The initial long-capture frame has already been added.");
        }

        if (!IsWithinLimits(frame.Width, frame.Height))
        {
            return new LongCaptureAddFrameResult(
                LongCaptureAddFrameStatus.LimitExceeded,
                0,
                0,
                0);
        }

        _canvas = frame.CopyPixels();
        _currentFrame = frame;
        _contentTop = 0;
        _contentBottom = frame.Height;
        _currentOffset = 0;
        return new LongCaptureAddFrameResult(
            LongCaptureAddFrameStatus.Initialized,
            frame.Width,
            frame.Height,
            frame.Height);
    }

    public LongCaptureAddFrameResult AddFrame(
        LongCaptureFrame frame,
        VerticalScrollDirection direction)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_currentFrame is null || _canvas is null)
        {
            return AddInitialFrame(frame);
        }

        var match = _matcher.Match(_currentFrame, frame, direction);
        var rejectedStatus = match.Status switch
        {
            OverlapMatchStatus.Ambiguous => LongCaptureAddFrameStatus.AmbiguousOverlap,
            OverlapMatchStatus.NoMatch => LongCaptureAddFrameStatus.NoOverlap,
            OverlapMatchStatus.IncompatibleFrames => LongCaptureAddFrameStatus.IncompatibleFrame,
            _ => (LongCaptureAddFrameStatus?)null,
        };
        if (rejectedStatus is not null)
        {
            return new LongCaptureAddFrameResult(
                rejectedStatus.Value,
                OutputWidth,
                OutputHeight,
                0,
                match);
        }

        var signedDisplacement = direction == VerticalScrollDirection.Down
            ? match.DisplacementPixels
            : -match.DisplacementPixels;
        var nextOffset = checked(_currentOffset + signedDisplacement);
        var nextTop = Math.Min(_contentTop, nextOffset);
        var nextBottom = Math.Max(_contentBottom, checked(nextOffset + frame.Height));
        var nextHeight = checked(nextBottom - nextTop);
        if (!IsWithinLimits(frame.Width, nextHeight))
        {
            return new LongCaptureAddFrameResult(
                LongCaptureAddFrameStatus.LimitExceeded,
                OutputWidth,
                OutputHeight,
                0,
                match);
        }

        var previousHeight = OutputHeight;
        var nextCanvas = GC.AllocateUninitializedArray<byte>(
            checked(frame.Width * nextHeight * LongCaptureFrame.BytesPerPixel));
        CopyRows(
            _canvas,
            frame.Width,
            previousHeight,
            nextCanvas,
            destinationStartRow: _contentTop - nextTop);
        CopyRows(
            frame.PixelSpan,
            frame.Width,
            frame.Height,
            nextCanvas,
            destinationStartRow: nextOffset - nextTop);

        _canvas = nextCanvas;
        _currentFrame = frame;
        _contentTop = nextTop;
        _contentBottom = nextBottom;
        _currentOffset = nextOffset;
        return new LongCaptureAddFrameResult(
            match.Status == OverlapMatchStatus.NoMovement
                ? LongCaptureAddFrameStatus.NoMovement
                : LongCaptureAddFrameStatus.Added,
            frame.Width,
            nextHeight,
            nextHeight - previousHeight,
            match);
    }

    public LongCaptureFrame BuildFrame()
    {
        if (_currentFrame is null || _canvas is null)
        {
            throw new InvalidOperationException("The long capture does not contain any frames.");
        }

        return new LongCaptureFrame(_currentFrame.Width, OutputHeight, _canvas);
    }

    private bool IsWithinLimits(int width, int height) =>
        width <= _maximumEdgePixels
        && height <= _maximumEdgePixels
        && checked((long)width * height) <= _maximumPixelCount;

    private static void CopyRows(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        Span<byte> destination,
        int destinationStartRow)
    {
        var bytesPerRow = checked(width * LongCaptureFrame.BytesPerPixel);
        source[..checked(bytesPerRow * height)].CopyTo(
            destination.Slice(
                checked(destinationStartRow * bytesPerRow),
                checked(height * bytesPerRow)));
    }
}
