namespace Crosio.Windows.Capture;

public sealed class ScreenshotCaptureException : Exception
{
    public ScreenshotCaptureException(string message)
        : base(message)
    {
    }

    public ScreenshotCaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ImageClipboardException : Exception
{
    public ImageClipboardException(string message)
        : base(message)
    {
    }

    public ImageClipboardException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class PinnedScreenshotException : Exception
{
    public PinnedScreenshotException(string message)
        : base(message)
    {
    }

    public PinnedScreenshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
