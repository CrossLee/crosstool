using Crosio.Windows.Platform.Images;
using Crosio.Windows.Platform.IO;

namespace Crosio.Windows.Platform.Shell;

public enum FileActivationOperation
{
    ShowMainWindow,
    BackgroundOnly,
    CompressImages,
    CopyPaths,
}

/// <summary>
/// A Windows-facing activation request. It intentionally does not duplicate the
/// app core's ActivationEnvelope; the app adapter converts this normalized input
/// into its own routing type.
/// </summary>
public sealed record FileActivationRequest(
    FileActivationOperation Operation,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> IgnoredArguments)
{
    public bool ShouldShowMainWindow => Operation == FileActivationOperation.ShowMainWindow;
}

public static class FileActivationRequestParser
{
    public const string CompressImagesSwitch = "--compress-images";
    public const string CopyPathsSwitch = "--copy-paths";
    public const string BackgroundSwitch = "--background";
    public const string ShowSwitch = "--show";

    public static FileActivationRequest Parse(IEnumerable<string>? arguments)
    {
        var args = arguments?.ToArray() ?? [];
        if (args.Length == 0)
        {
            return new FileActivationRequest(
                FileActivationOperation.ShowMainWindow,
                [],
                []);
        }

        var requestedOperation = FileActivationOperation.CompressImages;
        var hasExplicitOperation = false;
        var paths = new List<string>();
        var ignored = new List<string>();
        var parseOptions = true;

        foreach (var argument in args)
        {
            if (parseOptions && argument == "--")
            {
                parseOptions = false;
                continue;
            }

            if (parseOptions && argument.StartsWith("--", StringComparison.Ordinal))
            {
                switch (argument)
                {
                    case CompressImagesSwitch:
                        requestedOperation = FileActivationOperation.CompressImages;
                        hasExplicitOperation = true;
                        continue;
                    case CopyPathsSwitch:
                        requestedOperation = FileActivationOperation.CopyPaths;
                        hasExplicitOperation = true;
                        continue;
                    case BackgroundSwitch:
                        requestedOperation = FileActivationOperation.BackgroundOnly;
                        hasExplicitOperation = true;
                        continue;
                    case ShowSwitch:
                        requestedOperation = FileActivationOperation.ShowMainWindow;
                        hasExplicitOperation = true;
                        continue;
                    default:
                        ignored.Add(argument);
                        continue;
                }
            }

            if (IsUsableAbsolutePath(argument))
            {
                paths.Add(argument);
            }
            else
            {
                ignored.Add(argument);
            }
        }

        if (!hasExplicitOperation)
        {
            requestedOperation = paths.Count == 0
                ? FileActivationOperation.ShowMainWindow
                : FileActivationOperation.CompressImages;
        }
        else if (requestedOperation == FileActivationOperation.BackgroundOnly && paths.Count > 0)
        {
            // A startup launch may include paths only through an adapter bug. Do
            // not turn it into a foreground or file operation implicitly.
            ignored.AddRange(paths);
            paths.Clear();
        }

        return new FileActivationRequest(requestedOperation, paths, ignored);
    }

    public static FileActivationRequest FromImageFileActivation(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = paths
            .Where(IsUsableAbsolutePath)
            .Where(ImageCompressionPathPolicy.IsSupportedImagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new FileActivationRequest(
            FileActivationOperation.CompressImages,
            normalized,
            []);
    }

    private static bool IsUsableAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.IndexOf('\0') < 0 &&
        path.IndexOfAny(['\r', '\n']) < 0 &&
        WindowsPathPolicy.IsFullyQualified(path);
}
