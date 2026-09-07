using System.Text;

namespace Crosio.Windows.Core.Activation;

public static class CommandLineTokenizer
{
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
}
