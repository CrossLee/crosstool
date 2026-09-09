namespace Crosio.Windows.Translation;

public abstract class OfflineTranslationException : Exception
{
    protected OfflineTranslationException(string message)
        : base(message)
    {
    }

    protected OfflineTranslationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TranslationModelMissingException : OfflineTranslationException
{
    public TranslationModelMissingException(Crosio.Windows.Intelligence.Translation.TranslationDirection direction)
        : base(direction == Crosio.Windows.Intelligence.Translation.TranslationDirection.ChineseToEnglish
            ? "尚未安装中文到英文的离线翻译模型"
            : "尚未安装英文到简体中文的离线翻译模型")
    {
        Direction = direction;
    }

    public Crosio.Windows.Intelligence.Translation.TranslationDirection Direction { get; }
}

public sealed class TranslationModelInvalidException : OfflineTranslationException
{
    public TranslationModelInvalidException(string message)
        : base(message)
    {
    }

    public TranslationModelInvalidException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TranslationInputTooLongException : OfflineTranslationException
{
    public TranslationInputTooLongException(int actualTokens, int maximumTokens)
        : base($"文字过长：当前 {actualTokens} 个词元，离线模型最多支持 {maximumTokens} 个词元")
    {
        ActualTokens = actualTokens;
        MaximumTokens = maximumTokens;
    }

    public int ActualTokens { get; }
    public int MaximumTokens { get; }
}
