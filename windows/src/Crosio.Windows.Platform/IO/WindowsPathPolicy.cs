namespace Crosio.Windows.Platform.IO;

/// <summary>
/// Host-independent validation for ordinary drive-qualified and UNC Windows
/// paths. This keeps build-machine behavior identical on Windows and macOS CI.
/// Device namespace paths are deliberately excluded from shell activations.
/// </summary>
public static class WindowsPathPolicy
{
    public static bool IsFullyQualified(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.IndexOf('\0') >= 0 ||
            path.IndexOfAny(['\r', '\n']) >= 0)
        {
            return false;
        }

        if (path.Length >= 3 &&
            IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            IsSeparator(path[2]))
        {
            return true;
        }

        if (path.Length < 5 ||
            !IsSeparator(path[0]) ||
            !IsSeparator(path[1]) ||
            IsSeparator(path[2]) ||
            path[2] is '?' or '.')
        {
            return false;
        }

        var serverEnd = IndexOfSeparator(path, 2);
        if (serverEnd <= 2 || serverEnd >= path.Length - 1)
        {
            return false;
        }

        var shareEnd = IndexOfSeparator(path, serverEnd + 1);
        return shareEnd == -1
            ? serverEnd + 1 < path.Length
            : shareEnd > serverEnd + 1;
    }

    public static string GetExtension(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var filenameStart = LastSeparatorIndex(path) + 1;
        var dot = path.LastIndexOf('.');
        return dot <= filenameStart ? string.Empty : path[dot..];
    }

    public static (string DirectoryPrefix, string Stem, string Extension) SplitImagePath(
        string path)
    {
        if (!IsFullyQualified(path))
        {
            throw new ArgumentException("A fully qualified Windows path is required.", nameof(path));
        }

        var separator = LastSeparatorIndex(path);
        var filenameStart = separator + 1;
        var dot = path.LastIndexOf('.');
        if (separator < 0 || dot <= filenameStart || dot >= path.Length - 1)
        {
            throw new ArgumentException("The Windows path has no usable filename extension.", nameof(path));
        }

        return (
            path[..filenameStart],
            path[filenameStart..dot],
            path[dot..]);
    }

    private static int LastSeparatorIndex(string path) =>
        Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));

    private static int IndexOfSeparator(string path, int startIndex)
    {
        for (var index = startIndex; index < path.Length; index++)
        {
            if (IsSeparator(path[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsSeparator(char character) => character is '\\' or '/';

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
