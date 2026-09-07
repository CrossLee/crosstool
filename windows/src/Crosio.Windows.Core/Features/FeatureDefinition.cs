namespace Crosio.Windows.Core.Features;

public sealed record FeatureDefinition(
    FeatureId Id,
    string Route,
    string DisplayName,
    string Group,
    bool IsBackgroundCapable = false);
