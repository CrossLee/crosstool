using Crosio.Windows.Core.Presentation;

namespace Crosio.Windows.Core.Tests.Presentation;

public sealed class MainWindowVisibilityPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void KeepsMainWindowHiddenOnlyWhileVisualWorkCanCaptureIt(
        bool visualOperationInProgress,
        bool recordingInProgress,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindowVisibilityPolicy.ShouldKeepHidden(
                visualOperationInProgress,
                recordingInProgress));
    }

    [Fact]
    public void RecheckDetectsVisualOperationStartedAcrossAsyncBoundary()
    {
        var beforeAwait = MainWindowVisibilityPolicy.ShouldKeepHidden(
            visualOperationInProgress: false,
            recordingInProgress: false);
        var afterAwait = MainWindowVisibilityPolicy.ShouldKeepHidden(
            visualOperationInProgress: true,
            recordingInProgress: false);

        Assert.False(beforeAwait);
        Assert.True(afterAwait);
    }
}
