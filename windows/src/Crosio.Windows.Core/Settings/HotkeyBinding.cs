namespace Crosio.Windows.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
    Windows = 1 << 3,
}

public sealed record HotkeyBinding(HotkeyModifiers Modifiers, string Key)
{
    private const HotkeyModifiers SupportedModifiers =
        HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Windows;

    public bool TryNormalize(out HotkeyBinding normalized)
    {
        normalized = this;
        var key = Key?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(key)
            || (Modifiers & ~SupportedModifiers) != 0
            || !Modifiers.HasFlag(HotkeyModifiers.Control)
            || !IsSupportedKey(key))
        {
            return false;
        }

        normalized = new HotkeyBinding(Modifiers, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(Key.ToUpperInvariant());
        return string.Join('+', parts);
    }

    private static bool IsSupportedKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return true;
        }

        return key.Length is 2 or 3
            && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 24;
    }
}
