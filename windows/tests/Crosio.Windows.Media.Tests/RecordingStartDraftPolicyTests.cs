namespace Crosio.Windows.Media.Tests;

public sealed class RecordingStartDraftPolicyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void FailedStart_PreservesUncertainOrFinalizedDraft(
        bool nativeDisposalCompleted,
        bool finalizedContainerPresent,
        bool expected) =>
        Assert.Equal(
            expected,
            RecordingStartDraftPolicy.ShouldPreserveAfterFailedStart(
                nativeDisposalCompleted,
                finalizedContainerPresent));
}
