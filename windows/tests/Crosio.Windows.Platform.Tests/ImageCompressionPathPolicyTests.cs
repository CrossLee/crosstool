using Crosio.Windows.Platform.Images;

namespace Crosio.Windows.Platform.Tests;

public sealed class ImageCompressionPathPolicyTests
{
    [Theory]
    [InlineData(@"C:\Pictures\photo.jpg", @"C:\Pictures\photo-一爪.jpg")]
    [InlineData(@"C:\Pictures\photo.JPEG", @"C:\Pictures\photo-一爪.JPEG")]
    [InlineData(@"C:\Pictures\scan.tif", @"C:\Pictures\scan-一爪.tif")]
    [InlineData(@"\\server\share\photo.PNG", @"\\server\share\photo-一爪.PNG")]
    [InlineData(@"C:\图片\照片-crosio.HEIC", @"C:\图片\照片-crosio-一爪.HEIC")]
    [InlineData(@"C:\图片\照片-一爪.tIfF", @"C:\图片\照片-一爪-一爪.tIfF")]
    public void FirstCandidatePreservesSourceExtension(string source, string expected)
    {
        Assert.Equal(expected, ImageCompressionPathPolicy.CreateCandidatePath(source, 1));
    }

    [Fact]
    public void LaterCandidateUsesSequenceWithoutOverwritingEarlierResults()
    {
        var source = @"C:\Pictures\photo.png";

        var candidate = ImageCompressionPathPolicy.CreateCandidatePath(source, 7);

        Assert.Equal(@"C:\Pictures\photo-一爪-7.png", candidate);
    }

    [Theory]
    [InlineData("photo.webp")]
    [InlineData("photo.gif")]
    [InlineData("photo.svg")]
    public void UnsupportedFormatIsRejected(string path)
    {
        Assert.False(ImageCompressionPathPolicy.IsSupportedImagePath(path));
    }

    [Fact]
    public void CompressionResultReportsReduction()
    {
        var result = new ImageCompressionResult(
            @"C:\Pictures\source.jpg",
            @"C:\Pictures\source-一爪.jpg",
            1_000,
            250,
            100,
            100,
            MetTargetSize: true);

        Assert.Equal(750, result.SavedBytes);
        Assert.Equal(75, result.ReductionPercentage);
    }

    [Fact]
    public void SettingsRejectImpossibleTargetSize()
    {
        var settings = new ImageCompressionSettings(TargetBytes: 0);

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }
}
