using Crosio.Windows.Platform.Tray;

namespace Crosio.Windows.Platform.Tests;

public sealed class TrayRecordingMenuPolicyTests
{
    [Fact]
    public void HiddenStateOmitsRecordingItem()
    {
        Assert.Null(TrayRecordingMenuPolicy.ForState(TrayRecordingControlState.Hidden));
    }

    [Fact]
    public void StartingStateOffersCancel()
    {
        var item = Assert.IsType<TrayRecordingMenuItem>(
            TrayRecordingMenuPolicy.ForState(TrayRecordingControlState.CanCancel));

        Assert.Equal("取消录屏", item.Label);
        Assert.True(item.IsEnabled);
        Assert.Equal(TrayRecordingCommand.Cancel, item.Command);
    }

    [Fact]
    public void RecordingStateOffersStopAndSave()
    {
        var item = Assert.IsType<TrayRecordingMenuItem>(
            TrayRecordingMenuPolicy.ForState(TrayRecordingControlState.CanStopAndSave));

        Assert.Equal("停止并保存录屏", item.Label);
        Assert.True(item.IsEnabled);
        Assert.Equal(TrayRecordingCommand.StopAndSave, item.Command);
    }

    [Fact]
    public void StoppingStateShowsDisabledSavingStatus()
    {
        var item = Assert.IsType<TrayRecordingMenuItem>(
            TrayRecordingMenuPolicy.ForState(TrayRecordingControlState.Saving));

        Assert.Equal("正在保存录屏…", item.Label);
        Assert.False(item.IsEnabled);
        Assert.Null(item.Command);
    }
}
