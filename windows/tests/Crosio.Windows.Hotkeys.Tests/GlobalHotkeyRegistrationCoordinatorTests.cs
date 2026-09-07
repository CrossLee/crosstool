using Crosio.Windows.Core.Settings;

namespace Crosio.Windows.Hotkeys.Tests;

public sealed class GlobalHotkeyRegistrationCoordinatorTests
{
    [Fact]
    public void ReplacesBindingsAsOneSet()
    {
        var backend = new FakeBackend();
        using var coordinator = new GlobalHotkeyRegistrationCoordinator(backend);

        var result = coordinator.ReplaceBindings(Bindings(("capture-region", "1"), ("capture-window", "2")));

        Assert.True(result.Succeeded);
        Assert.Equal(2, coordinator.ActiveBindings.Count);
        Assert.Equal(2, backend.Registered.Count);
    }

    [Fact]
    public void RegistrationFailureRestoresPreviousSet()
    {
        var backend = new FakeBackend();
        using var coordinator = new GlobalHotkeyRegistrationCoordinator(backend);
        Assert.True(coordinator.ReplaceBindings(Bindings(("capture-region", "1"))).Succeeded);
        backend.VirtualKeyToRejectOnce = '2';

        var result = coordinator.ReplaceBindings(Bindings(("capture-window", "2"), ("capture-screen", "3")));

        Assert.False(result.Succeeded);
        Assert.Single(coordinator.ActiveBindings);
        Assert.Contains("capture-region", coordinator.ActiveBindings.Keys);
        Assert.Single(backend.Registered);
        Assert.Equal((uint)'1', backend.Registered.Values.Single().VirtualKey);
    }

    [Fact]
    public void DuplicateBindingsAreRejectedBeforeChangingNativeState()
    {
        var backend = new FakeBackend();
        using var coordinator = new GlobalHotkeyRegistrationCoordinator(backend);
        Assert.True(coordinator.ReplaceBindings(Bindings(("capture-region", "1"))).Succeeded);
        var callsBefore = backend.RegisterCalls;

        var duplicate = new Dictionary<string, HotkeyBinding>
        {
            ["capture-window"] = Binding("2"),
            ["capture-screen"] = Binding("2"),
        };
        var result = coordinator.ReplaceBindings(duplicate);

        Assert.False(result.Succeeded);
        Assert.Equal(callsBefore, backend.RegisterCalls);
        Assert.Contains("capture-region", coordinator.ActiveBindings.Keys);
    }

    [Fact]
    public void DisposeUnregistersEverything()
    {
        var backend = new FakeBackend();
        var coordinator = new GlobalHotkeyRegistrationCoordinator(backend);
        Assert.True(coordinator.ReplaceBindings(Bindings(("capture-region", "1"), ("capture-window", "2"))).Succeeded);

        coordinator.Dispose();

        Assert.Empty(backend.Registered);
    }

    private static Dictionary<string, HotkeyBinding> Bindings(params (string Route, string Key)[] pairs) =>
        pairs.ToDictionary(pair => pair.Route, pair => Binding(pair.Key));

    private static HotkeyBinding Binding(string key) =>
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, key);

    private sealed class FakeBackend : IHotkeyRegistrationBackend
    {
        public Dictionary<int, NativeHotkey> Registered { get; } = [];

        public uint? VirtualKeyToRejectOnce { get; set; }

        public int RegisterCalls { get; private set; }

        public bool TryRegister(int id, NativeHotkey hotkey, out int nativeErrorCode)
        {
            RegisterCalls++;
            if (VirtualKeyToRejectOnce == hotkey.VirtualKey)
            {
                VirtualKeyToRejectOnce = null;
                nativeErrorCode = 1409;
                return false;
            }

            Registered.Add(id, hotkey);
            nativeErrorCode = 0;
            return true;
        }

        public void Unregister(int id) => Registered.Remove(id);
    }
}
