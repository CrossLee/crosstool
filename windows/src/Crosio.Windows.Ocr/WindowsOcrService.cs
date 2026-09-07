using Crosio.Windows.Intelligence.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Crosio.Windows.Ocr;

public sealed class WindowsOcrService : IImageOcrService
{
    private static readonly string[] DefaultLanguageTags = ["zh-Hans", "en-US"];

    public async Task<OcrDocument> RecognizeAsync(
        string imagePath,
        IReadOnlyList<string>? preferredLanguages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到需要 OCR 的图片", fullPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(fullPath);
        await using var source = await file.OpenStreamForReadAsync().ConfigureAwait(false);
        using var randomAccess = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccess);

        return await RecognizeDecoderAsync(
            decoder,
            preferredLanguages,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OcrDocument> RecognizeEncodedAsync(
        ReadOnlyMemory<byte> encodedImage,
        IReadOnlyList<string>? preferredLanguages = null,
        CancellationToken cancellationToken = default)
    {
        if (encodedImage.IsEmpty)
        {
            throw new ArgumentException("图片内容不能为空", nameof(encodedImage));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var randomAccess = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccess))
        {
            writer.WriteBytes(encodedImage.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        randomAccess.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(randomAccess);

        return await RecognizeDecoderAsync(
            decoder,
            preferredLanguages,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OcrDocument> RecognizeDecoderAsync(
        BitmapDecoder decoder,
        IReadOnlyList<string>? preferredLanguages,
        CancellationToken cancellationToken)
    {
        var engine = CreateEngine(preferredLanguages ?? DefaultLanguageTags)
            ?? throw new InvalidOperationException("请在 Windows 语言设置中安装中文或英文 OCR 语言包");

        var width = checked((int)decoder.OrientedPixelWidth);
        var height = checked((int)decoder.OrientedPixelHeight);
        var maximumDimension = checked((int)OcrEngine.MaxImageDimension);
        var tiles = OcrTilePlanner.CreateTiles(width, height, maximumDimension);
        var recognized = new List<RecognizedTextLine>();

        foreach (var tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds
                {
                    X = checked((uint)tile.X),
                    Y = checked((uint)tile.Y),
                    Width = checked((uint)tile.Width),
                    Height = checked((uint)tile.Height),
                },
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            var result = await engine.RecognizeAsync(bitmap);

            foreach (var line in result.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                var bounds = BoundsForLine(line, tile);
                recognized.Add(new RecognizedTextLine(line.Text, bounds, Confidence: 1));
            }
        }

        var merged = OcrLineMerger.Merge(recognized);
        return new OcrDocument(
            string.Join(Environment.NewLine, merged.Select(line => line.Text)),
            merged,
            width,
            height);
    }

    public static IReadOnlyList<string> AvailableRecognizerLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray();

    private static OcrEngine? CreateEngine(IEnumerable<string> preferredLanguageTags)
    {
        foreach (var tag in preferredLanguageTags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
        {
            try
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language))
                {
                    var engine = OcrEngine.TryCreateFromLanguage(language);
                    if (engine is not null)
                    {
                        return engine;
                    }
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static OcrRectangle BoundsForLine(OcrLine line, OcrRectangle tile)
    {
        var words = line.Words;
        if (words.Count == 0)
        {
            return new OcrRectangle(tile.X, tile.Y, 0, 0);
        }

        var left = words.Min(word => word.BoundingRect.Left);
        var top = words.Min(word => word.BoundingRect.Top);
        var right = words.Max(word => word.BoundingRect.Right);
        var bottom = words.Max(word => word.BoundingRect.Bottom);
        return new OcrRectangle(
            checked(tile.X + (int)Math.Floor(left)),
            checked(tile.Y + (int)Math.Floor(top)),
            Math.Max(1, checked((int)Math.Ceiling(right - left))),
            Math.Max(1, checked((int)Math.Ceiling(bottom - top))));
    }
}
