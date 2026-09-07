namespace Crosio.Windows.Intelligence.Ocr;

public readonly record struct OcrRectangle(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
}

public sealed record RecognizedTextLine(string Text, OcrRectangle Bounds, double Confidence);

public sealed record OcrDocument(
    string Text,
    IReadOnlyList<RecognizedTextLine> Lines,
    int ImageWidth,
    int ImageHeight);

public interface IImageOcrService
{
    Task<OcrDocument> RecognizeAsync(
        string imagePath,
        IReadOnlyList<string>? preferredLanguages = null,
        CancellationToken cancellationToken = default);

    Task<OcrDocument> RecognizeEncodedAsync(
        ReadOnlyMemory<byte> encodedImage,
        IReadOnlyList<string>? preferredLanguages = null,
        CancellationToken cancellationToken = default);
}
