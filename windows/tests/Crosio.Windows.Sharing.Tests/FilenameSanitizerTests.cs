using Xunit;

namespace Crosio.Windows.Sharing.Tests;

public sealed class FilenameSanitizerTests
{
    [Theory]
    [InlineData("课堂资料 01.png", "课堂资料 01.png")]
    [InlineData("../../secret.txt", "secret.txt")]
    [InlineData("..\\..\\secret.txt", "secret.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("bad:name?.jpg", "bad_name_.jpg")]
    [InlineData("...", "未命名文件")]
    public void SanitizeProducesPortableLeafName(string input, string expected)
    {
        Assert.Equal(expected, FilenameSanitizer.Sanitize(input));
    }

    [Fact]
    public void SanitizePreservesExtensionWithinLengthLimit()
    {
        var result = FilenameSanitizer.Sanitize(new string('图', 200) + ".png");

        Assert.Equal(180, result.Length);
        Assert.EndsWith(".png", result, StringComparison.Ordinal);
    }
}
