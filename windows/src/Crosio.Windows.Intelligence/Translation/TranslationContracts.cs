namespace Crosio.Windows.Intelligence.Translation;

public enum TranslationDirection
{
    ChineseToEnglish,
    EnglishToChinese,
}

public enum TranslationInputIssue
{
    None,
    Empty,
    Unsupported,
}

public sealed record TranslationInput(
    string Text,
    TranslationDirection? Direction,
    TranslationInputIssue Issue)
{
    public bool CanTranslate => Issue == TranslationInputIssue.None && Direction is not null;
}

public sealed record TranslationResult(
    string SourceText,
    string TranslatedText,
    TranslationDirection Direction,
    DateTimeOffset CreatedAt);

public interface ITextTranslationEngine
{
    Task<string> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken cancellationToken = default);
}

public sealed class TranslationException : Exception
{
    public TranslationException(string message) : base(message)
    {
    }

    public TranslationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
