namespace Crosio.Windows.Media;

public sealed record PreparedRecordingRequest(
    Guid SessionId,
    RecordingRequest Request,
    RecordingEncodingProfile EncodingProfile,
    RecordingSafetyLimits SafetyLimits,
    string DraftPath);

public sealed record RecordingBackendResult(
    string DraftPath,
    TimeSpan Duration,
    long FileBytes,
    bool ContainerFinalized);

public interface IActiveRecording : IAsyncDisposable
{
    DateTimeOffset StartedAt { get; }

    string DraftPath { get; }

    Task<RecordingBackendResult> StopAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by production backends that continuously enforce recording
/// limits. The controller observes this signal and finalizes the MP4 without
/// requiring another click from the user.
/// </summary>
public interface IRecordingRuntimeLimitSource
{
    Task<RecordingRuntimeLimit> RuntimeLimitReached { get; }
}

/// <summary>
/// Signals that the native capture ended without a controller Stop call (for
/// example, because a captured window closed or the native encoder failed).
/// The task is a notification only; StopAsync remains the authoritative result
/// and propagates success or failure.
/// </summary>
public interface IRecordingRuntimeEndSource
{
    Task RuntimeEnded { get; }
}

public interface IRecordingBackend
{
    ValueTask<RecordingCapabilityReport> CheckCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    Task<IActiveRecording> StartAsync(
        PreparedRecordingRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRecordingFileStore
{
    string GetVolumeIdentity(string path);

    long GetAvailableBytes(string completedDirectory);

    string CreateDraftPath(Guid sessionId, RecordingEncodingProfile profile);

    string PromoteFinalizedDraft(
        string draftPath,
        string completedDirectory,
        string suggestedBaseName,
        RecordingEncodingProfile profile);

    void DeleteDraftIfPresent(string draftPath);
}

public sealed class RecordingFileStore : IRecordingFileStore
{
    private readonly string _draftDirectory;

    public RecordingFileStore(string draftDirectory)
    {
        _draftDirectory = string.IsNullOrWhiteSpace(draftDirectory)
            ? throw new ArgumentException("A draft directory is required.", nameof(draftDirectory))
            : Path.GetFullPath(draftDirectory);
    }

    public long GetAvailableBytes(string completedDirectory)
    {
        var root = GetVolumeIdentity(completedDirectory);
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public string GetVolumeIdentity(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.GetPathRoot(fullPath)
            ?? throw new RecordingException(
                RecordingFailureCode.InvalidRequest,
                "The recording destination does not have a filesystem root.");
    }

    public string CreateDraftPath(Guid sessionId, RecordingEncodingProfile profile)
    {
        Directory.CreateDirectory(_draftDirectory);
        return Path.Combine(_draftDirectory, $"{sessionId:N}.partial{profile.FileExtension}");
    }

    public string PromoteFinalizedDraft(
        string draftPath,
        string completedDirectory,
        string suggestedBaseName,
        RecordingEncodingProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedBaseName);

        var fullDraftPath = Path.GetFullPath(draftPath);
        var allowedDraftRoot = EnsureTrailingSeparator(_draftDirectory);
        if (!fullDraftPath.StartsWith(allowedDraftRoot, PathComparison) ||
            Directory.Exists(fullDraftPath) ||
            IsSymbolicLink(fullDraftPath))
        {
            throw new RecordingException(
                RecordingFailureCode.FinalizationFailed,
                "Only regular recording drafts owned by 一爪 can be finalized.");
        }

        if (!File.Exists(fullDraftPath))
        {
            throw new RecordingException(
                RecordingFailureCode.FinalizationFailed,
                "The finalized recording draft does not exist.");
        }

        if (new FileInfo(fullDraftPath).Length <= 0)
        {
            throw new RecordingException(
                RecordingFailureCode.FinalizationFailed,
                "The finalized recording draft is empty.");
        }

        var completedRoot = Path.GetFullPath(completedDirectory);
        Directory.CreateDirectory(completedRoot);
        var safeBaseName = SanitizeBaseName(suggestedBaseName);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var candidate = Path.Combine(completedRoot, $"{safeBaseName} {timestamp}{profile.FileExtension}");

        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(
                completedRoot,
                $"{safeBaseName} {timestamp} ({suffix}){profile.FileExtension}");
        }

        File.Move(fullDraftPath, candidate);
        return candidate;
    }

    public void DeleteDraftIfPresent(string draftPath)
    {
        if (string.IsNullOrWhiteSpace(draftPath))
        {
            return;
        }

        var fullDraftPath = Path.GetFullPath(draftPath);
        if (!fullDraftPath.StartsWith(EnsureTrailingSeparator(_draftDirectory), PathComparison))
        {
            return;
        }

        if (File.Exists(fullDraftPath) && !IsSymbolicLink(fullDraftPath))
        {
            File.Delete(fullDraftPath);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string EnsureTrailingSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    private static bool IsSymbolicLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string SanitizeBaseName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .TrimEnd('.', ' ');

        return string.IsNullOrWhiteSpace(sanitized) ? "一爪录屏" : sanitized;
    }
}
