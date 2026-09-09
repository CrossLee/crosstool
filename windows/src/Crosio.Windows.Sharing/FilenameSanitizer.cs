using System.Text;

namespace Crosio.Windows.Sharing;

public static class FilenameSanitizer
{
    private static readonly HashSet<char> InvalidCharacters =
        new("<>:\"/\\|?*\0".Concat(Enumerable.Range(0, 32).Select(value => (char)value)));

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? proposedName)
    {
        var portablePath = (proposedName ?? string.Empty).Replace('\\', '/');
        var lastComponent = portablePath[(portablePath.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(lastComponent.Length);

        foreach (var character in lastComponent)
        {
            builder.Append(InvalidCharacters.Contains(character) ? '_' : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length == 0 || result is "." or "..")
        {
            result = "未命名文件";
        }

        var extension = Path.GetExtension(result);
        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem))
        {
            result = $"_{result}";
        }

        const int maximumLength = 180;
        if (result.Length > maximumLength)
        {
            extension = Path.GetExtension(result);
            stem = Path.GetFileNameWithoutExtension(result);
            var stemLength = Math.Max(1, maximumLength - extension.Length);
            result = string.Concat(stem.AsSpan(0, Math.Min(stem.Length, stemLength)), extension);
        }

        return result;
    }
}
