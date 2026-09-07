using System.Globalization;

namespace Crosio.Windows.Media;

public readonly record struct HslColor(double Hue, double Saturation, double Lightness)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"hsl({Math.Round(Hue):0}, {Math.Round(Saturation * 100):0}%, {Math.Round(Lightness * 100):0}%)");
}

public readonly record struct SampledColor(byte Red, byte Green, byte Blue, byte Alpha = byte.MaxValue)
{
    public string Hex => Alpha == byte.MaxValue
        ? string.Create(CultureInfo.InvariantCulture, $"#{Red:X2}{Green:X2}{Blue:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{Red:X2}{Green:X2}{Blue:X2}{Alpha:X2}");

    public string Rgb => Alpha == byte.MaxValue
        ? string.Create(CultureInfo.InvariantCulture, $"rgb({Red}, {Green}, {Blue})")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"rgba({Red}, {Green}, {Blue}, {Alpha / 255D:0.###})");

    public HslColor Hsl
    {
        get
        {
            var red = Red / 255D;
            var green = Green / 255D;
            var blue = Blue / 255D;
            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            var delta = maximum - minimum;
            var lightness = (maximum + minimum) / 2D;

            if (delta == 0)
            {
                return new HslColor(0, 0, lightness);
            }

            var saturation = delta / (1D - Math.Abs((2D * lightness) - 1D));
            var hueSection = maximum switch
            {
                var value when value == red => ((green - blue) / delta) % 6D,
                var value when value == green => ((blue - red) / delta) + 2D,
                _ => ((red - green) / delta) + 4D,
            };
            var hue = hueSection * 60D;
            if (hue < 0)
            {
                hue += 360D;
            }

            return new HslColor(hue, saturation, lightness);
        }
    }

    public string HslText => Hsl.ToString();
}

public sealed record ScreenColorSample(
    PixelPoint Position,
    SampledColor Color,
    DateTimeOffset SampledAt);

public interface IScreenColorSampler
{
    ValueTask<ScreenColorSample> SampleAsync(
        PixelPoint position,
        CancellationToken cancellationToken = default);

    ValueTask<ScreenColorSample> SampleCursorAsync(CancellationToken cancellationToken = default);
}

public enum ColorTextFormat
{
    Hex,
    Rgb,
    Hsl,
}

public interface ITextClipboardWriter
{
    Task WriteTextAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class ColorClipboardService
{
    private readonly ITextClipboardWriter _clipboardWriter;

    public ColorClipboardService(ITextClipboardWriter clipboardWriter)
    {
        _clipboardWriter = clipboardWriter ?? throw new ArgumentNullException(nameof(clipboardWriter));
    }

    public Task CopyAsync(
        SampledColor color,
        ColorTextFormat format,
        CancellationToken cancellationToken = default)
    {
        var text = format switch
        {
            ColorTextFormat.Hex => color.Hex,
            ColorTextFormat.Rgb => color.Rgb,
            ColorTextFormat.Hsl => color.HslText,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        return _clipboardWriter.WriteTextAsync(text, cancellationToken);
    }
}

public sealed class RecentColorStore
{
    public const int MaximumCount = 12;

    private readonly object _sync = new();
    private readonly List<SampledColor> _colors = [];

    public IReadOnlyList<SampledColor> Colors
    {
        get
        {
            lock (_sync)
            {
                return _colors.ToArray();
            }
        }
    }

    public void Confirm(SampledColor color)
    {
        lock (_sync)
        {
            _colors.Remove(color);
            _colors.Insert(0, color);
            if (_colors.Count > MaximumCount)
            {
                _colors.RemoveRange(MaximumCount, _colors.Count - MaximumCount);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _colors.Clear();
        }
    }
}
