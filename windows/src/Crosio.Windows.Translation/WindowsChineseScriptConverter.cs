using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Crosio.Windows.Translation;

internal static class WindowsChineseScriptConverter
{
    private const uint SimplifiedChinese = 0x02000000;

    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text) || !OperatingSystem.IsWindows())
        {
            return text;
        }

        var requiredCharacters = LCMapStringEx(
            "zh-CN",
            SimplifiedChinese,
            text,
            text.Length,
            null,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (requiredCharacters <= 0)
        {
            throw new TranslationModelInvalidException(
                "Windows 无法将离线翻译结果规范为简体中文",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var output = new char[requiredCharacters];
        var written = LCMapStringEx(
            "zh-CN",
            SimplifiedChinese,
            text,
            text.Length,
            output,
            output.Length,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (written <= 0)
        {
            throw new TranslationModelInvalidException(
                "Windows 无法将离线翻译结果规范为简体中文",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return new string(output, 0, written);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string localeName,
        uint mapFlags,
        string source,
        int sourceLength,
        [Out] char[]? destination,
        int destinationLength,
        IntPtr versionInformation,
        IntPtr reserved,
        IntPtr sortHandle);
}
