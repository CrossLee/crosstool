using Crosio.Windows.Core.Settings;

namespace Crosio.Windows.Hotkeys;

public sealed class GlobalHotkeyPressedEventArgs : EventArgs
{
    public GlobalHotkeyPressedEventArgs(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        Route = route;
    }

    public string Route { get; }
}

public sealed record GlobalHotkeyRegistrationFailure(
    string Route,
    HotkeyBinding Binding,
    int NativeErrorCode,
    string Message);

public sealed record GlobalHotkeyUpdateResult(
    bool Succeeded,
    GlobalHotkeyRegistrationFailure? Failure,
    IReadOnlyDictionary<string, HotkeyBinding> ActiveBindings)
{
    public static GlobalHotkeyUpdateResult Success(IReadOnlyDictionary<string, HotkeyBinding> bindings) =>
        new(true, null, bindings);

    public static GlobalHotkeyUpdateResult Failed(
        GlobalHotkeyRegistrationFailure failure,
        IReadOnlyDictionary<string, HotkeyBinding> bindings) =>
        new(false, failure, bindings);
}

public interface IGlobalHotkeyHost : IDisposable
{
    event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;

    IReadOnlyDictionary<string, HotkeyBinding> ActiveBindings { get; }

    GlobalHotkeyUpdateResult ReplaceBindings(IReadOnlyDictionary<string, HotkeyBinding> bindings);
}
