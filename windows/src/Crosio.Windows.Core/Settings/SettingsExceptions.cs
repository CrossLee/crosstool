namespace Crosio.Windows.Core.Settings;

public sealed class SettingsFileException(string path, Exception innerException)
    : IOException($"The Crosio settings file could not be read: {path}", innerException)
{
    public string SettingsPath { get; } = path;
}

public sealed class SettingsValidationException(IReadOnlyList<SettingsValidationIssue> issues)
    : Exception("The Crosio settings are invalid.")
{
    public IReadOnlyList<SettingsValidationIssue> Issues { get; } = issues;
}
