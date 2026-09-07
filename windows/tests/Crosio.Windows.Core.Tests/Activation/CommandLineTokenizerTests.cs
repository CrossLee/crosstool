using Crosio.Windows.Core.Activation;

namespace Crosio.Windows.Core.Tests.Activation;

public sealed class CommandLineTokenizerTests
{
    [Fact]
    public void KeepsQuotedWindowsPathsTogether()
    {
        var arguments = CommandLineTokenizer.Tokenize(
            "--compress \"C:\\课件\\图片 1.png\" C:\\Temp\\two.jpg");

        Assert.Equal(
            new[] { "--compress", @"C:\课件\图片 1.png", @"C:\Temp\two.jpg" },
            arguments);
    }

    [Fact]
    public void PreservesEscapedQuote()
    {
        var arguments = CommandLineTokenizer.Tokenize("--feature \"capture-\\\"region\"");

        Assert.Equal(new[] { "--feature", "capture-\"region" }, arguments);
    }
}
