using Crosio.Windows.Core.Activation;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Crosio.Windows.App;

internal static class WindowsActivationAdapter
{
    public static ActivationEnvelope ToEnvelope(AppActivationArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Kind == ExtendedActivationKind.File
            && arguments.Data is IFileActivatedEventArgs fileArguments)
        {
            return ActivationEnvelope.OpenFiles(
                fileArguments.Files.Select(file => file.Path));
        }

        if (arguments.Kind == ExtendedActivationKind.Protocol
            && arguments.Data is IProtocolActivatedEventArgs protocolArguments)
        {
            return ActivationEnvelope.Protocol(protocolArguments.Uri.AbsoluteUri);
        }

        if (arguments.Kind == ExtendedActivationKind.StartupTask)
        {
            return ActivationEnvelope.Background();
        }

        if (arguments.Kind == ExtendedActivationKind.Launch
            && arguments.Data is ILaunchActivatedEventArgs launchArguments)
        {
            var tokens = CommandLineTokenizer.Tokenize(launchArguments.Arguments);
            return tokens.Count == 0
                ? ActivationEnvelope.Launch()
                : ActivationEnvelope.CommandLine(tokens);
        }

        return ActivationEnvelope.Launch();
    }
}
