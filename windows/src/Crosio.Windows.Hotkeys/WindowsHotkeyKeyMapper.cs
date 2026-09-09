using Crosio.Windows.Core.Settings;

namespace Crosio.Windows.Hotkeys;

public readonly record struct NativeHotkey(uint Modifiers, uint VirtualKey);

public static class WindowsHotkeyKeyMapper
{
    public const uint NoRepeatModifier = 0x4000;
    private const uint AltModifier = 0x0001;
    private const uint ControlModifier = 0x0002;
    private const uint ShiftModifier = 0x0004;
    private const uint WindowsModifier = 0x0008;

    public static bool TryMap(HotkeyBinding binding, out NativeHotkey native)
    {
        native = default;
        if (binding is null || !binding.TryNormalize(out var normalized))
        {
            return false;
        }

        var virtualKey = MapVirtualKey(normalized.Key);
        if (virtualKey is null)
        {
            return false;
        }

        uint modifiers = NoRepeatModifier;
        if (normalized.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= ControlModifier;
        if (normalized.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= ShiftModifier;
        if (normalized.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= AltModifier;
        if (normalized.Modifiers.HasFlag(HotkeyModifiers.Windows)) modifiers |= WindowsModifier;
        native = new NativeHotkey(modifiers, virtualKey.Value);
        return true;
    }

    private static uint? MapVirtualKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return key[0];
        }

        if (key.Length is 2 or 3 &&
            key[0] == 'F' &&
            int.TryParse(key.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            return checked((uint)(0x70 + functionKey - 1));
        }

        return null;
    }
}
