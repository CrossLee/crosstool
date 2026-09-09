using System.Globalization;
using System.Text;

namespace Crosio.Windows.Intelligence.Translation;

public static class TranslationDirectionDetector
{
    public static TranslationInput Analyze(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new TranslationInput(string.Empty, null, TranslationInputIssue.Empty);
        }

        var hasHan = false;
        var hasLatinLetter = false;
        foreach (var rune in text.EnumerateRunes())
        {
            hasHan |= IsHan(rune.Value);
            hasLatinLetter |= IsLatinLetter(rune);
        }

        if (hasHan)
        {
            return new TranslationInput(text, TranslationDirection.ChineseToEnglish, TranslationInputIssue.None);
        }
        if (hasLatinLetter)
        {
            return new TranslationInput(text, TranslationDirection.EnglishToChinese, TranslationInputIssue.None);
        }
        return new TranslationInput(text, null, TranslationInputIssue.Unsupported);
    }

    private static bool IsHan(int value) =>
        value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2FA1F;

    private static bool IsLatinLetter(Rune rune)
    {
        if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            return true;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is not (UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter))
        {
            return false;
        }

        return rune.Value is >= 0x00C0 and <= 0x024F
            or >= 0x1E00 and <= 0x1EFF;
    }
}
