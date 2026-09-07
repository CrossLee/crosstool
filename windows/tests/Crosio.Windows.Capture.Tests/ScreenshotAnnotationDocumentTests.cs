using Crosio.Windows.Capture.Editor;

namespace Crosio.Windows.Capture.Tests;

public sealed class ScreenshotAnnotationDocumentTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void AddUndoRedoAndResetMaintainVersionedHistory()
    {
        using var document = CreateDocument();
        document.Add(new PenAnnotation(
            [new ScreenshotPoint(0, 0)],
            unchecked((int)0xffff0000),
            width: 1));

        Assert.True(document.HasAnnotations);
        Assert.True(document.CanUndo);
        Assert.Equal(1, document.Version);
        Assert.True(document.Undo());
        Assert.False(document.HasAnnotations);
        Assert.True(document.CanRedo);
        Assert.True(document.Redo());
        Assert.True(document.HasAnnotations);
        Assert.True(document.Reset());
        Assert.False(document.HasAnnotations);
        Assert.True(document.Undo());
        Assert.True(document.HasAnnotations);
        Assert.Equal(5, document.Version);
    }

    [Fact]
    public void OutputSnapshotKeepsOriginalPixelDimensionsAndVersion()
    {
        using var document = CreateDocument();
        document.Add(new RectangleAnnotation(
            new ScreenshotPoint(0, 0),
            new ScreenshotPoint(0, 0),
            unchecked((int)0xff00ff00),
            width: 1));

        var snapshot = document.CreateOutputSnapshot();

        Assert.Equal(1, snapshot.Version);
        Assert.Equal(1, snapshot.Image.PixelWidth);
        Assert.Equal(1, snapshot.Image.PixelHeight);
        Assert.True(snapshot.Image.PngBytes.Length > 8);
    }

    private static ScreenshotAnnotationDocument CreateDocument() => new(
        new CapturedImage(OnePixelPng, pixelWidth: 1, pixelHeight: 1));
}
