namespace Crosio.Windows.Sharing;

public enum SharedItemKind
{
    File,
    Image,
    Text,
}

public enum SharedItemDirection
{
    Outgoing,
    Incoming,
}

public sealed record SharedItem(
    Guid Id,
    SharedItemKind Kind,
    SharedItemDirection Direction,
    string Title,
    string? Detail,
    string? FilePath,
    long? ByteCount,
    string MimeType,
    DateTimeOffset CreatedAt,
    string? RemoteAddress);

public enum SharedContentErrorCode
{
    InvalidContent,
    ItemTooLarge,
    InboxQuotaExceeded,
    InsufficientDiskSpace,
    IncomingItemLimitExceeded,
    IncomingTextQuotaExceeded,
    UnsafeInboxEntry,
}

public sealed class SharedContentException : Exception
{
    public SharedContentException(
        string message,
        SharedContentErrorCode code = SharedContentErrorCode.InvalidContent,
        Exception? innerException = null) : base(message, innerException)
    {
        Code = code;
    }

    public SharedContentErrorCode Code { get; }
}
