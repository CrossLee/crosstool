namespace Crosio.Windows.Media;

public static class NativeRecordingFailureClassifier
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = EnumerateExceptionChain(exception).ToArray();

        if (chain.Any(error => error is BadImageFormatException))
        {
            return "[recording-architecture-mismatch] 录屏组件与当前 Crosio 架构不匹配，请安装对应的 x64 或 ARM64 版本。";
        }

        if (chain.Any(error =>
                error.Message.Contains("MSVCP140", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("VCRUNTIME140", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("CONCRT140", StringComparison.OrdinalIgnoreCase)) ||
            chain.Any(error => error is DllNotFoundException))
        {
            return "[vc-runtime-missing] 缺少与 Crosio 架构匹配的 Microsoft Visual C++ 2015-2022 运行库，录屏无法启动。";
        }

        if (chain.Any(error => error is FileNotFoundException &&
                error.Message.Contains("ScreenRecorderLib", StringComparison.OrdinalIgnoreCase)))
        {
            return "[recording-component-missing] Crosio 的原生录屏组件缺失，安装包可能不完整，请重新安装对应架构版本。";
        }

        if (chain.Any(error =>
                error.Message.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("MF_E_", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("0xC00D", StringComparison.OrdinalIgnoreCase)))
        {
            return "[media-foundation-unavailable] Windows Media Foundation 或 H.264/AAC 编码器不可用；Windows N/KN 请安装 Media Feature Pack。";
        }

        var root = chain.LastOrDefault() ?? exception;
        return $"[screen-recorder-native-load-failed] Windows 录屏组件无法加载：{root.GetType().Name}: {root.Message}";
    }

    private static IEnumerable<Exception> EnumerateExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
