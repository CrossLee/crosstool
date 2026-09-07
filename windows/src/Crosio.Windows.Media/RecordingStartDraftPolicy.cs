namespace Crosio.Windows.Media;

internal static class RecordingStartDraftPolicy
{
    public static bool ShouldPreserveAfterFailedStart(
        bool nativeDisposalCompleted,
        string draftPath) =>
        ShouldPreserveAfterFailedStart(
            nativeDisposalCompleted,
            Mp4ContainerInspector.IsFinalized(draftPath));

    internal static bool ShouldPreserveAfterFailedStart(
        bool nativeDisposalCompleted,
        bool finalizedContainerPresent) =>
        !nativeDisposalCompleted || finalizedContainerPresent;
}
