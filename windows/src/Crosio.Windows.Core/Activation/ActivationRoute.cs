using Crosio.Windows.Core.Features;

namespace Crosio.Windows.Core.Activation;

public enum ActivationOperation
{
    Navigate,
    CompressImages,
    CopyPaths,
}

public enum ActivationPresentation
{
    ShowMainWindow,
    KeepInBackground,
}

public enum ActivationIssue
{
    None,
    MissingArgument,
    UnknownFeature,
    UnsupportedProtocol,
    UnsupportedArguments,
}

public sealed class ActivationRoute
{
    public ActivationRoute(
        ActivationOperation operation,
        FeatureId feature,
        ActivationPresentation presentation,
        IEnumerable<string>? inputs = null,
        ActivationIssue issue = ActivationIssue.None)
    {
        Operation = operation;
        Feature = feature;
        Presentation = presentation;
        Inputs = Array.AsReadOnly(inputs?.ToArray() ?? Array.Empty<string>());
        Issue = issue;
    }

    public ActivationOperation Operation { get; }

    public FeatureId Feature { get; }

    public ActivationPresentation Presentation { get; }

    public IReadOnlyList<string> Inputs { get; }

    public ActivationIssue Issue { get; }

    public bool ShouldShowMainWindow => Presentation == ActivationPresentation.ShowMainWindow;

    public static ActivationRoute Navigate(FeatureId feature, IEnumerable<string>? inputs = null) =>
        new(ActivationOperation.Navigate, feature, ActivationPresentation.ShowMainWindow, inputs);

    public static ActivationRoute Invalid(ActivationIssue issue) =>
        new(ActivationOperation.Navigate, FeatureId.Home, ActivationPresentation.ShowMainWindow, issue: issue);
}
