using Crosio.Windows.Core.Settings;

namespace Crosio.Windows.Hotkeys.Tests;

public sealed class WindowsHotkeyKeyMapperTests
{
    [Theory]
    [InlineData("A", 0x41u)]
    [InlineData("1", 0x31u)]
    [InlineData("F1", 0x70u)]
    [InlineData("F24", 0x87u)]
    public void MapsSupportedKeys(string key, uint expectedVirtualKey)
    {
        var binding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, key);

        var mapped = WindowsHotkeyKeyMapper.TryMap(binding, out var native);

        Assert.True(mapped);
        Assert.Equal(expectedVirtualKey, native.VirtualKey);
        Assert.NotEqual(0u, native.Modifiers & WindowsHotkeyKeyMapper.NoRepeatModifier);
    }

    [Fact]
    public void RejectsUnsafeBindingWithoutControl()
    {
        var binding = new HotkeyBinding(HotkeyModifiers.Windows, "A");

        Assert.False(WindowsHotkeyKeyMapper.TryMap(binding, out _));
    }
}
