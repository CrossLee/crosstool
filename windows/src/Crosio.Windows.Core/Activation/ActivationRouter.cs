using Crosio.Windows.Core.Features;

namespace Crosio.Windows.Core.Activation;

public sealed class ActivationRouter
{
    public ActivationRoute Route(ActivationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return envelope.Kind switch
        {
            ActivationKind.Launch => ActivationRoute.Navigate(FeatureId.Home),
            ActivationKind.Background => new ActivationRoute(
                ActivationOperation.Navigate,
                FeatureId.Home,
                ActivationPresentation.KeepInBackground),
            ActivationKind.OpenFiles => RouteOpenFiles(envelope.Arguments),
            ActivationKind.Protocol => RouteProtocol(envelope.Arguments),
            ActivationKind.CommandLine => RouteCommandLine(envelope.Arguments),
            _ => ActivationRoute.Invalid(ActivationIssue.UnsupportedArguments),
        };
    }

    private static ActivationRoute RouteOpenFiles(IReadOnlyList<string> paths) =>
        paths.Count == 0
            ? ActivationRoute.Invalid(ActivationIssue.MissingArgument)
            : new ActivationRoute(
                ActivationOperation.CompressImages,
                FeatureId.ImageCompression,
                ActivationPresentation.KeepInBackground,
                paths);

    private ActivationRoute RouteCommandLine(IReadOnlyList<string> arguments)
    {
        var tokens = arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)).ToArray();
        if (tokens.Length == 0)
        {
            return ActivationRoute.Navigate(FeatureId.Home);
        }

        return tokens[0].ToLowerInvariant() switch
        {
            "--background" => new ActivationRoute(
                ActivationOperation.Navigate,
                FeatureId.Home,
                ActivationPresentation.KeepInBackground),
            "--show" => ActivationRoute.Navigate(FeatureId.Home),
            "--feature" => RouteFeature(tokens.Skip(1).FirstOrDefault()),
            "--compress" or "--compress-images" => RouteBackgroundOperation(
                ActivationOperation.CompressImages,
                FeatureId.ImageCompression,
                tokens.Skip(1)),
            "--copy-path" or "--copy-paths" => RouteBackgroundOperation(
                ActivationOperation.CopyPaths,
                FeatureId.Home,
                tokens.Skip(1)),
            "--protocol" => RouteProtocol(tokens.Skip(1).Take(1).ToArray()),
            _ when Uri.TryCreate(tokens[0], UriKind.Absolute, out var uri)
                && uri.Scheme.Equals("crosio", StringComparison.OrdinalIgnoreCase) => RouteProtocol(new[] { tokens[0] }),
            _ when tokens.All(token => !token.StartsWith("--", StringComparison.Ordinal)) => RouteOpenFiles(tokens),
            _ => ActivationRoute.Invalid(ActivationIssue.UnsupportedArguments),
        };
    }

    private static ActivationRoute RouteFeature(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return ActivationRoute.Invalid(ActivationIssue.MissingArgument);
        }

        return FeatureCatalog.TryGetByRoute(route, out var feature)
            ? ActivationRoute.Navigate(feature.Id)
            : ActivationRoute.Invalid(ActivationIssue.UnknownFeature);
    }

    private static ActivationRoute RouteBackgroundOperation(
        ActivationOperation operation,
        FeatureId feature,
        IEnumerable<string> inputs)
    {
        var values = inputs.Where(input => !string.IsNullOrWhiteSpace(input)).ToArray();
        return values.Length == 0
            ? ActivationRoute.Invalid(ActivationIssue.MissingArgument)
            : new ActivationRoute(operation, feature, ActivationPresentation.KeepInBackground, values);
    }

    private static ActivationRoute RouteProtocol(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0
            || !Uri.TryCreate(arguments[0], UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("crosio", StringComparison.OrdinalIgnoreCase))
        {
            return ActivationRoute.Invalid(ActivationIssue.UnsupportedProtocol);
        }

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host == "feature")
        {
            return segments.Length == 0
                ? ActivationRoute.Invalid(ActivationIssue.MissingArgument)
                : RouteFeature(segments[0]);
        }

        if (host == "translate")
        {
            var text = QueryValues(uri, "text").FirstOrDefault();
            return ActivationRoute.Navigate(
                FeatureId.TextTranslation,
                text is null ? null : new[] { text });
        }

        if (host == "compress")
        {
            // Image compression is intentionally available only through an
            // explicit file activation or a local command-line invocation.
            // A public URI could otherwise make a web page silently touch a
            // UNC path after the browser's one-time protocol confirmation.
            return ActivationRoute.Invalid(ActivationIssue.UnsupportedProtocol);
        }

        if (host == "share" && segments.Length > 0)
        {
            return segments[0].ToLowerInvariant() switch
            {
                "files" => ActivationRoute.Navigate(FeatureId.ShareFiles),
                "text" => ActivationRoute.Navigate(FeatureId.ShareText),
                _ => ActivationRoute.Invalid(ActivationIssue.UnknownFeature),
            };
        }

        return FeatureCatalog.TryGetByRoute(host, out var directFeature)
            ? ActivationRoute.Navigate(directFeature.Id)
            : ActivationRoute.Invalid(ActivationIssue.UnsupportedProtocol);
    }

    private static IReadOnlyList<string> QueryValues(Uri uri, string requestedKey)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            if (!Uri.UnescapeDataString(pair[0]).Equals(requestedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var encodedValue = pair.Length == 2 ? pair[1] : string.Empty;
            values.Add(Uri.UnescapeDataString(encodedValue.Replace('+', ' ')));
        }

        return values;
    }
}
