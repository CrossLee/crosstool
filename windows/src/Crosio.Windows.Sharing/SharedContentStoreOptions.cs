namespace Crosio.Windows.Sharing;

public sealed record SharedContentStoreOptions(
    long MaximumUploadBytes = SharedContentStore.MaximumUploadBytes,
    long MaximumInboxBytes = SharedContentStore.MaximumInboxBytes,
    long MinimumFreeDiskBytes = SharedContentStore.MinimumFreeDiskBytes,
    int MaximumIncomingItems = SharedContentStore.MaximumIncomingItems,
    long MaximumIncomingTextBytes = SharedContentStore.MaximumIncomingTextBytes)
{
    internal void Validate()
    {
        if (MaximumUploadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumUploadBytes));
        }
        if (MaximumInboxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumInboxBytes));
        }
        if (MinimumFreeDiskBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeDiskBytes));
        }
        if (MaximumIncomingItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumIncomingItems));
        }
        if (MaximumIncomingTextBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumIncomingTextBytes));
        }
    }
}

internal interface IInboxDiskSpaceProbe
{
    long AvailableFreeBytes(string path);
}

internal sealed class DriveInfoInboxDiskSpaceProbe : IInboxDiskSpaceProbe
{
    public long AvailableFreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("无法确定收件箱所在磁盘。");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}
