using System.Security.Cryptography;
using System.Text;

namespace Crosio.Windows.Translation;

internal static class ModelPackPathPolicy
{
    public const string ManifestFileName = "manifest.json";
    private static readonly char[] PortableInvalidFileNameCharacters =
        ['<', '>', ':', '"', '|', '?', '*', '\0'];

    public static string DirectionKey(Crosio.Windows.Intelligence.Translation.TranslationDirection direction) =>
        direction == Crosio.Windows.Intelligence.Translation.TranslationDirection.ChineseToEnglish
            ? "zh-en"
            : "en-zh";

    public static string PackDirectoryName(string packId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(packId));
        return $"pack-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new TranslationModelInvalidException("模型文件路径不能为空");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new TranslationModelInvalidException($"模型文件路径必须是相对路径：{path}");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsUnsafeSegment))
        {
            throw new TranslationModelInvalidException($"模型文件路径不安全：{path}");
        }

        return string.Join('/', segments);
    }

    public static string ResolveWithin(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationModelInvalidException($"模型文件路径越界：{relativePath}");
        }
        return candidate;
    }

    private static bool IsUnsafeSegment(string segment)
    {
        if (segment is "." or ".." || segment.Length > 240 ||
            segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            return true;
        }
        if (segment.IndexOfAny(PortableInvalidFileNameCharacters) >= 0 ||
            segment.Any(char.IsControl))
        {
            return true;
        }

        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 &&
             (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             stem[3] is >= '1' and <= '9');
    }
}
