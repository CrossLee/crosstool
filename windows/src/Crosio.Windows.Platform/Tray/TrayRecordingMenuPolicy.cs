namespace Crosio.Windows.Platform.Tray;

public enum TrayRecordingControlState
{
    Hidden,
    CanCancel,
    CanStopAndSave,
    Saving,
}

public enum TrayRecordingCommand
{
    Cancel,
    StopAndSave,
}

public sealed class TrayRecordingCommandRequestedEventArgs : EventArgs
{
    public TrayRecordingCommandRequestedEventArgs(TrayRecordingCommand command)
    {
        Command = command;
    }

    public TrayRecordingCommand Command { get; }
}

public sealed record TrayRecordingMenuItem(
    string Label,
    bool IsEnabled,
    TrayRecordingCommand? Command);

public static class TrayRecordingMenuPolicy
{
    public static TrayRecordingMenuItem? ForState(TrayRecordingControlState state) => state switch
    {
        TrayRecordingControlState.Hidden => null,
        TrayRecordingControlState.CanCancel => new TrayRecordingMenuItem(
            "取消录屏",
            IsEnabled: true,
            TrayRecordingCommand.Cancel),
        TrayRecordingControlState.CanStopAndSave => new TrayRecordingMenuItem(
            "停止并保存录屏",
            IsEnabled: true,
            TrayRecordingCommand.StopAndSave),
        TrayRecordingControlState.Saving => new TrayRecordingMenuItem(
            "正在保存录屏…",
            IsEnabled: false,
            Command: null),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
