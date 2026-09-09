using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Crosio.Windows.Platform.Shell;

public interface IExplorerRevealService
{
    void RevealFiles(IReadOnlyList<string> paths);
}

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ExplorerRevealService : IExplorerRevealService
{
    public void RevealFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var existingPaths = paths
            .Where(static path =>
                !string.IsNullOrWhiteSpace(path) &&
                Path.IsPathFullyQualified(path) &&
                File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (existingPaths.Length == 0)
        {
            return;
        }

        foreach (var group in existingPaths.GroupBy(
            static path => Path.GetDirectoryName(path)!,
            StringComparer.OrdinalIgnoreCase))
        {
            RevealOneFolder(group.Key, group.ToArray());
        }
    }

    private static void RevealOneFolder(string folderPath, IReadOnlyList<string> itemPaths)
    {
        var folderPidl = ILCreateFromPath(folderPath);
        if (folderPidl == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Windows could not resolve folder '{folderPath}'.");
        }

        var absoluteItemPidls = new IntPtr[itemPaths.Count];
        try
        {
            var relativeItemPidls = new IntPtr[itemPaths.Count];
            for (var index = 0; index < itemPaths.Count; index++)
            {
                absoluteItemPidls[index] = ILCreateFromPath(itemPaths[index]);
                if (absoluteItemPidls[index] == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"Windows could not resolve output file '{itemPaths[index]}'.");
                }

                // The returned pointer remains valid while the corresponding
                // absolute PIDL is retained below.
                relativeItemPidls[index] = ILFindLastID(absoluteItemPidls[index]);
            }

            var result = SHOpenFolderAndSelectItems(
                folderPidl,
                checked((uint)relativeItemPidls.Length),
                relativeItemPidls,
                0);
            Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            foreach (var itemPidl in absoluteItemPidls)
            {
                if (itemPidl != IntPtr.Zero)
                {
                    ILFree(itemPidl);
                }
            }

            ILFree(folderPidl);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "ILCreateFromPathW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string path);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr folderPidl,
        uint itemCount,
        [In] IntPtr[] itemPidls,
        uint flags);
}
