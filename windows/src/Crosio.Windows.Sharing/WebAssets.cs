using System.Reflection;

namespace Crosio.Windows.Sharing;

internal sealed class WebAssets
{
    private const string Prefix = "Crosio.Windows.Sharing.Web.";
    private readonly Assembly _assembly = typeof(WebAssets).Assembly;

    public Stream Open(string filename) =>
        _assembly.GetManifestResourceStream(Prefix + filename)
        ?? throw new InvalidOperationException($"缺少内嵌网页资源：{filename}");

    public async Task<string> ReadTextAsync(string filename, CancellationToken cancellationToken)
    {
        await using var stream = Open(filename);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
