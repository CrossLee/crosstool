namespace Crosio.Windows.Accessibility;

public enum SelectedTextIssue
{
    None,
    EmptySelection,
    UnsupportedControl,
    AccessDenied,
    TimedOut,
    TargetClosed,
    Failed,
}

public sealed record SelectedTextResult(string? Text, SelectedTextIssue Issue, string? Message)
{
    public bool IsSuccess => Issue == SelectedTextIssue.None && !string.IsNullOrWhiteSpace(Text);

    public static SelectedTextResult Success(string text) => new(text, SelectedTextIssue.None, null);

    public static SelectedTextResult Failure(SelectedTextIssue issue, string message) =>
        new(null, issue, message);
}

public interface ISelectedTextReader
{
    Task<SelectedTextResult> ReadAsync(CancellationToken cancellationToken = default);
}
