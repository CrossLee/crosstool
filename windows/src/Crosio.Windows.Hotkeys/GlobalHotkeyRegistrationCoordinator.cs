using Crosio.Windows.Core.Settings;

namespace Crosio.Windows.Hotkeys;

public interface IHotkeyRegistrationBackend
{
    bool TryRegister(int id, NativeHotkey hotkey, out int nativeErrorCode);

    void Unregister(int id);
}

public sealed class GlobalHotkeyRegistrationCoordinator : IDisposable
{
    private const int FirstRegistrationId = 0x4350;
    private readonly IHotkeyRegistrationBackend _backend;
    private readonly Dictionary<int, Registration> _active = [];
    private bool _disposed;

    public GlobalHotkeyRegistrationCoordinator(IHotkeyRegistrationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public IReadOnlyDictionary<string, HotkeyBinding> ActiveBindings => _active.Values
        .ToDictionary(entry => entry.Route, entry => entry.Binding, StringComparer.OrdinalIgnoreCase);

    public string? RouteForRegistrationId(int registrationId) =>
        _active.TryGetValue(registrationId, out var registration) ? registration.Route : null;

    public GlobalHotkeyUpdateResult ReplaceBindings(IReadOnlyDictionary<string, HotkeyBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bindings);

        var desired = Normalize(bindings, out var validationFailure);
        if (validationFailure is not null)
        {
            return GlobalHotkeyUpdateResult.Failed(validationFailure, ActiveBindings);
        }

        var previous = _active.Values.OrderBy(entry => entry.Id).ToArray();
        UnregisterAll(_active.Values);
        _active.Clear();

        var newlyRegistered = new List<Registration>();
        foreach (var registration in desired!)
        {
            if (_backend.TryRegister(registration.Id, registration.Native, out var errorCode))
            {
                newlyRegistered.Add(registration);
                _active.Add(registration.Id, registration);
                continue;
            }

            UnregisterAll(newlyRegistered);
            _active.Clear();
            RestoreOrThrow(previous);
            var failure = new GlobalHotkeyRegistrationFailure(
                registration.Route,
                registration.Binding,
                errorCode,
                errorCode == 1409
                    ? "这个快捷键已经被其他程序占用"
                    : $"Windows 无法注册这个快捷键（错误 {errorCode}）");
            return GlobalHotkeyUpdateResult.Failed(failure, ActiveBindings);
        }

        return GlobalHotkeyUpdateResult.Success(ActiveBindings);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterAll(_active.Values);
        _active.Clear();
        _disposed = true;
    }

    private static Registration[]? Normalize(
        IReadOnlyDictionary<string, HotkeyBinding> bindings,
        out GlobalHotkeyRegistrationFailure? failure)
    {
        failure = null;
        var occupied = new HashSet<HotkeyBinding>();
        var registrations = new List<Registration>(bindings.Count);
        var id = FirstRegistrationId;

        foreach (var pair in bindings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Value is null ||
                !pair.Value.TryNormalize(out var normalized) ||
                !WindowsHotkeyKeyMapper.TryMap(pair.Value, out var native))
            {
                failure = new GlobalHotkeyRegistrationFailure(
                    pair.Key ?? string.Empty,
                    pair.Value ?? new HotkeyBinding(HotkeyModifiers.None, string.Empty),
                    87,
                    "快捷键格式不受支持");
                return null;
            }

            if (!occupied.Add(normalized))
            {
                failure = new GlobalHotkeyRegistrationFailure(
                    pair.Key,
                    normalized,
                    87,
                    "两个功能不能使用同一个快捷键");
                return null;
            }

            registrations.Add(new Registration(id++, pair.Key, normalized, native));
        }

        return registrations.ToArray();
    }

    private void RestoreOrThrow(IEnumerable<Registration> previous)
    {
        foreach (var registration in previous)
        {
            if (!_backend.TryRegister(registration.Id, registration.Native, out var errorCode))
            {
                UnregisterAll(_active.Values);
                _active.Clear();
                throw new InvalidOperationException(
                    $"无法恢复原有全局快捷键 {registration.Binding}（错误 {errorCode}）");
            }

            _active.Add(registration.Id, registration);
        }
    }

    private void UnregisterAll(IEnumerable<Registration> registrations)
    {
        foreach (var registration in registrations.ToArray())
        {
            _backend.Unregister(registration.Id);
        }
    }

    private sealed record Registration(
        int Id,
        string Route,
        HotkeyBinding Binding,
        NativeHotkey Native);
}
