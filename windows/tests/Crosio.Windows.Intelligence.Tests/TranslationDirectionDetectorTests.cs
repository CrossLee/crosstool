using Crosio.Windows.Intelligence.Translation;
using Xunit;

namespace Crosio.Windows.Intelligence.Tests;

public sealed class TranslationDirectionDetectorTests
{
    [Theory]
    [InlineData("你好，welcome", TranslationDirection.ChineseToEnglish)]
    [InlineData("Hello, Crosio.", TranslationDirection.EnglishToChinese)]
    [InlineData("Résumé", TranslationDirection.EnglishToChinese)]
    public void AnalyzeChoosesExpectedDirection(string text, TranslationDirection direction)
    {
        var result = TranslationDirectionDetector.Analyze(text);

        Assert.True(result.CanTranslate);
        Assert.Equal(direction, result.Direction);
    }

    [Theory]
    [InlineData("", TranslationInputIssue.Empty)]
    [InlineData("   ", TranslationInputIssue.Empty)]
    [InlineData("12345", TranslationInputIssue.Unsupported)]
    [InlineData("✅ → 42", TranslationInputIssue.Unsupported)]
    public void AnalyzeRejectsEmptyOrDirectionlessContent(string text, TranslationInputIssue issue)
    {
        var result = TranslationDirectionDetector.Analyze(text);

        Assert.False(result.CanTranslate);
        Assert.Equal(issue, result.Issue);
    }
}
