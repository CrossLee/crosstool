using System.Collections.ObjectModel;
using Crosio.Windows.Core.Features;

namespace Crosio.Windows.Core.Settings;

public sealed record HotkeyCommandDefinition(
    FeatureId Feature,
    string Route,
    string Title,
    string Group,
    bool IsRequired);

/// <summary>
/// The complete, stable set of user-configurable global commands. Optional
/// commands are represented by an absent dictionary entry rather than by an
/// invalid or empty key binding.
/// </summary>
public static class HotkeyCommandCatalog
{
    private static readonly IReadOnlyList<HotkeyCommandDefinition> Definitions =
        new ReadOnlyCollection<HotkeyCommandDefinition>(
        [
            Define(FeatureId.RegionScreenshot, "截图", isRequired: true),
            Define(FeatureId.WindowScreenshot, "截图", isRequired: true),
            Define(FeatureId.ScreenScreenshot, "截图", isRequired: true),
            Define(FeatureId.DelayedScreenshot, "截图"),
            Define(FeatureId.FramedScreenshot, "截图"),
            Define(FeatureId.MultiWindowScreenshot, "截图"),
            Define(FeatureId.LongScreenshot, "截图"),
            Define(FeatureId.ScreenRecording, "录屏"),
            Define(FeatureId.RegionRecording, "录屏"),
            Define(FeatureId.WindowRecording, "录屏"),
            Define(FeatureId.ColorPicker, "工具"),
            Define(FeatureId.TextTranslation, "工具", displayName: "快捷翻译"),
        ]);

    private static readonly IReadOnlyDictionary<string, HotkeyCommandDefinition> ByRoute =
        Definitions.ToDictionary(
            definition => definition.Route,
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<HotkeyCommandDefinition> All => Definitions;

    public static IReadOnlySet<string> RequiredRoutes { get; } = Definitions
        .Where(definition => definition.IsRequired)
        .Select(definition => definition.Route)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string route, out HotkeyCommandDefinition definition) =>
        ByRoute.TryGetValue(route, out definition!);

    private static HotkeyCommandDefinition Define(
        FeatureId feature,
        string group,
        bool isRequired = false,
        string? displayName = null)
    {
        var definition = FeatureCatalog.Get(feature);
        return new HotkeyCommandDefinition(
            feature,
            definition.Route,
            displayName ?? definition.DisplayName,
            group,
            isRequired);
    }
}
