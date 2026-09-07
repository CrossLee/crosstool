namespace Crosio.Windows.Core.Activation;

public enum ActivationKind
{
    Launch,
    CommandLine,
    Protocol,
    OpenFiles,
    Background,
}

public sealed class ActivationEnvelope
{
    public ActivationEnvelope(ActivationKind kind, IEnumerable<string>? arguments = null)
    {
        Kind = kind;
        Arguments = Array.AsReadOnly(arguments?.ToArray() ?? Array.Empty<string>());
    }

    public ActivationKind Kind { get; }

    public IReadOnlyList<string> Arguments { get; }

    public static ActivationEnvelope Launch() => new(ActivationKind.Launch);

    public static ActivationEnvelope Background() => new(ActivationKind.Background);

    public static ActivationEnvelope CommandLine(IEnumerable<string> arguments) =>
        new(ActivationKind.CommandLine, arguments);

    public static ActivationEnvelope Protocol(string uri) =>
        new(ActivationKind.Protocol, new[] { uri });

    public static ActivationEnvelope OpenFiles(IEnumerable<string> paths) =>
        new(ActivationKind.OpenFiles, paths);
}
