namespace Crosio.Windows.Sharing;

public static class MimeTypes
{
    private static readonly IReadOnlyDictionary<string, string> KnownTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".bmp"] = "image/bmp",
            [".gif"] = "image/gif",
            [".heic"] = "image/heic",
            [".heif"] = "image/heif",
            [".jpeg"] = "image/jpeg",
            [".jpg"] = "image/jpeg",
            [".jfif"] = "image/jpeg",
            [".png"] = "image/png",
            [".svg"] = "image/svg+xml",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".webp"] = "image/webp",
            [".csv"] = "text/csv; charset=utf-8",
            [".html"] = "text/html; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".md"] = "text/markdown; charset=utf-8",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain; charset=utf-8",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".mp3"] = "audio/mpeg",
            [".mp4"] = "video/mp4",
            [".mov"] = "video/quicktime",
            [".zip"] = "application/zip",
        };

    public static string ForFile(string path) =>
        KnownTypes.TryGetValue(Path.GetExtension(path), out var value)
            ? value
            : "application/octet-stream";

    public static SharedItemKind KindForFile(string path) =>
        ForFile(path).StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? SharedItemKind.Image
            : SharedItemKind.File;
}
