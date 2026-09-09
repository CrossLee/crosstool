namespace Crosio.Windows.Core.Presentation;

public static class MainWindowVisibilityPolicy
{
    public static bool ShouldKeepHidden(
        bool visualOperationInProgress,
        bool recordingInProgress) =>
        visualOperationInProgress || recordingInProgress;
}
