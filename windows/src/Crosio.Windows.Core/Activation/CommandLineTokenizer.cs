using System.Text;

namespace Crosio.Windows.Core.Activation;

public static class CommandLineTokenizer
{
    /// <summary>
    /// Tokenizes a Windows App SDK AppLifecycle launch payload and removes the
    /// executable token when the payload contains the complete Win32 command
    /// line. Packaged activation payloads can already be argument-only, so the
    /// first token is removed only when it identifies the current executable.
    /// </summary>
    public static IReadOnlyList<string> TokenizeLaunchArguments(
        string? commandLine,
        string? executablePath)
    {
        var arguments = Tokenize(commandLine);
        if (arguments.Count == 0 ||
            !IdentifiesExecutable(arguments[0], executablePath))
        {
            return arguments;
        }

        return Array.AsReadOnly(arguments.Skip(1).ToArray());
    }

    public static IReadOnlyList<string> Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Array.Empty<string>();
        }

        var arguments = new List<string>();
        var index = 0;

        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
            {
                index++;
            }

            if (index >= commandLine.Length)
            {
                break;
            }

            var value = new StringBuilder();
            var inQuotes = false;

            while (index < commandLine.Length && (inQuotes || !char.IsWhiteSpace(commandLine[index])))
            {
                var slashCount = 0;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    slashCount++;
                    index++;
                }

                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    value.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                    }
                    else
                    {
                        value.Append('"');
                    }

                    index++;
                    continue;
                }

                value.Append('\\', slashCount);
                if (index < commandLine.Length)
                {
                    value.Append(commandLine[index]);
                    index++;
                }
            }

            arguments.Add(value.ToString());
        }

        return arguments;
    }

    private static bool IdentifiesExecutable(string candidate, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        if (string.Equals(candidate, executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var candidateName = WindowsFileName(candidate);
        var executableName = WindowsFileName(executablePath);
        return executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidateName, executableName, StringComparison.OrdinalIgnoreCase);
    }

    private static string WindowsFileName(string path)
    {
        var separator = path.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? path : path[(separator + 1)..];
    }
}
