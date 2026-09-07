using System.Collections.ObjectModel;

namespace Crosio.Windows.Core.Features;

public static class FeatureCatalog
{
    private static readonly IReadOnlyList<FeatureDefinition> Definitions = new ReadOnlyCollection<FeatureDefinition>(
        new[]
        {
            new FeatureDefinition(FeatureId.Home, "home", "首页", "主页"),
            new FeatureDefinition(FeatureId.RegionScreenshot, "capture-region", "区域截图", "截图", true),
            new FeatureDefinition(FeatureId.WindowScreenshot, "capture-window", "窗口截图", "截图", true),
            new FeatureDefinition(FeatureId.ScreenScreenshot, "capture-screen", "全屏截图", "截图", true),
            new FeatureDefinition(FeatureId.DelayedScreenshot, "capture-delay", "延时截图", "截图", true),
            new FeatureDefinition(FeatureId.FramedScreenshot, "capture-frame", "带壳截图", "截图", true),
            new FeatureDefinition(FeatureId.MultiWindowScreenshot, "capture-multi-window", "多窗口截图", "截图", true),
            new FeatureDefinition(FeatureId.LongScreenshot, "capture-scroll", "长截图", "截图", true),
            new FeatureDefinition(FeatureId.ScreenRecording, "record-screen", "录制屏幕", "录屏", true),
            new FeatureDefinition(FeatureId.RegionRecording, "record-region", "录制区域", "录屏", true),
            new FeatureDefinition(FeatureId.WindowRecording, "record-window", "录制窗口", "录屏", true),
            new FeatureDefinition(FeatureId.ColorPicker, "color-picker", "提取颜色", "工具", true),
            new FeatureDefinition(FeatureId.TextTranslation, "translate", "文本翻译", "工具", true),
            new FeatureDefinition(FeatureId.ImageCompression, "compress-images", "图片压缩", "工具", true),
            new FeatureDefinition(FeatureId.ShareFiles, "share-files", "分享文件", "课堂共享", true),
            new FeatureDefinition(FeatureId.ShareText, "share-text", "分享文字", "课堂共享", true),
            new FeatureDefinition(FeatureId.Settings, "settings", "设置", "设置"),
        });

    private static readonly IReadOnlyDictionary<string, FeatureDefinition> ByRoute = Definitions
        .ToDictionary(definition => definition.Route, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<FeatureDefinition> All => Definitions;

    public static FeatureDefinition Get(FeatureId id) =>
        Definitions.First(definition => definition.Id == id);

    public static bool TryGetByRoute(string? route, out FeatureDefinition definition)
    {
        if (route is not null
            && ByRoute.TryGetValue(NormalizeRoute(route), out var matchedDefinition))
        {
            definition = matchedDefinition;
            return true;
        }

        definition = null!;
        return false;
    }

    public static string NormalizeRoute(string route) =>
        route.Trim().Trim('/').Replace('_', '-').ToLowerInvariant();
}
