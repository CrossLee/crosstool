namespace Crosio.Windows.Core.Settings;

public sealed class SettingsFileException(string path, Exception innerException)
    : IOException($"The 一爪 settings file could not be read: {path}", innerException)
{
    public string SettingsPath { get; } = path;
}

public sealed class SettingsValidationException(IReadOnlyList<SettingsValidationIssue> issues)
    : Exception("The 一爪 settings are invalid.")
{
    public IReadOnlyList<SettingsValidationIssue> Issues { get; } = issues;
}
