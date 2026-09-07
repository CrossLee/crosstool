using System.Diagnostics;

namespace Crosio.Windows.Translation;

internal static class BestEffortFileSystemCleanup
{
    public static bool TryRun(
        string description,
        Action cleanup,
        Action<string>? debugWriter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(cleanup);

        try
        {
            cleanup();
            return true;
        }
        catch (Exception error)
        {
            var message = $"Best-effort cleanup failed for {description}: {error}";
            if (debugWriter is null)
            {
                Debug.WriteLine(message);
            }
            else
            {
                debugWriter(message);
            }
            return false;
        }
    }
}
