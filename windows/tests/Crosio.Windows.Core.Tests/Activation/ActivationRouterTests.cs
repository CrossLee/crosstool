using Crosio.Windows.Core.Activation;
using Crosio.Windows.Core.Features;

namespace Crosio.Windows.Core.Tests.Activation;

public sealed class ActivationRouterTests
{
    private readonly ActivationRouter _router = new();

    [Fact]
    public void NormalLaunchShowsHome()
    {
        var route = _router.Route(ActivationEnvelope.Launch());

        Assert.Equal(ActivationOperation.Navigate, route.Operation);
        Assert.Equal(FeatureId.Home, route.Feature);
        Assert.True(route.ShouldShowMainWindow);
    }

    [Fact]
    public void UnpackagedFullCommandLineLaunchDoesNotTreatExecutableAsAFile()
    {
        const string executable = @"C:\Program Files\Crosio\Crosio.exe";
        var arguments = CommandLineTokenizer.TokenizeLaunchArguments(
            $"\"{executable}\"",
            executable);

        var route = _router.Route(ActivationEnvelope.CommandLine(arguments));

        Assert.Equal(ActivationOperation.Navigate, route.Operation);
        Assert.Equal(FeatureId.Home, route.Feature);
        Assert.True(route.ShouldShowMainWindow);
        Assert.Empty(route.Inputs);
    }

    [Theory]
    [InlineData("--copy-paths", ActivationOperation.CopyPaths)]
    [InlineData("--compress-images", ActivationOperation.CompressImages)]
    public void UnpackagedFullCommandLinePreservesExplicitSilentOperations(
        string operation,
        ActivationOperation expectedOperation)
    {
        const string executable = @"C:\Program Files\Crosio\Crosio.exe";
        var arguments = CommandLineTokenizer.TokenizeLaunchArguments(
            $"\"{executable}\" {operation} \"C:\\课件\\图片 1.png\"",
            executable);

        var route = _router.Route(ActivationEnvelope.CommandLine(arguments));

        Assert.Equal(expectedOperation, route.Operation);
        Assert.False(route.ShouldShowMainWindow);
        Assert.Equal(@"C:\课件\图片 1.png", Assert.Single(route.Inputs));
    }

    [Fact]
    public void FileActivationCompressesWithoutShowingMainWindow()
    {
        var paths = new[] { @"C:\课件\图片 1.png", @"C:\课件\图片 2.jpg" };

        var route = _router.Route(ActivationEnvelope.OpenFiles(paths));

        Assert.Equal(ActivationOperation.CompressImages, route.Operation);
        Assert.Equal(FeatureId.ImageCompression, route.Feature);
        Assert.False(route.ShouldShowMainWindow);
        Assert.Equal(paths, route.Inputs);
    }

    [Fact]
    public void CopyPathCommandRunsInBackground()
    {
        var route = _router.Route(ActivationEnvelope.CommandLine(
            new[] { "--copy-path", @"C:\资料", @"C:\资料\讲义.docx" }));

        Assert.Equal(ActivationOperation.CopyPaths, route.Operation);
        Assert.False(route.ShouldShowMainWindow);
        Assert.Equal(2, route.Inputs.Count);
    }

    [Theory]
    [InlineData("--copy-path")]
    [InlineData("--copy-paths")]
    public void ShellCopyPathAliasesStaySilent(string operation)
    {
        var route = _router.Route(ActivationEnvelope.CommandLine(
            new[] { operation, @"C:\资料\讲义.docx" }));

        Assert.Equal(ActivationOperation.CopyPaths, route.Operation);
        Assert.False(route.ShouldShowMainWindow);
    }

    [Theory]
    [InlineData("--compress")]
    [InlineData("--compress-images")]
    public void ImageCompressionAliasesStaySilent(string operation)
    {
        var route = _router.Route(ActivationEnvelope.CommandLine(
            new[] { operation, @"C:\图片\课堂.jpg" }));

        Assert.Equal(ActivationOperation.CompressImages, route.Operation);
        Assert.False(route.ShouldShowMainWindow);
    }

    [Fact]
    public void TranslationProtocolCarriesDecodedText()
    {
        var route = _router.Route(ActivationEnvelope.Protocol(
            "crosio://translate?text=Hello%2C+Windows"));

        Assert.Equal(FeatureId.TextTranslation, route.Feature);
        Assert.True(route.ShouldShowMainWindow);
        Assert.Equal("Hello, Windows", Assert.Single(route.Inputs));
    }

    [Fact]
    public void PublicProtocolCannotSilentlyCompressLocalOrUncPaths()
    {
        var local = _router.Route(ActivationEnvelope.Protocol(
            "crosio://compress?path=C%3A%5CUsers%5CStudent%5Cphoto.png"));
        var unc = _router.Route(ActivationEnvelope.Protocol(
            "crosio://compress?path=%5C%5Cattacker.example%5Cshare%5Cphoto.png"));

        Assert.Equal(ActivationIssue.UnsupportedProtocol, local.Issue);
        Assert.Equal(ActivationIssue.UnsupportedProtocol, unc.Issue);
        Assert.True(local.ShouldShowMainWindow);
        Assert.True(unc.ShouldShowMainWindow);
        Assert.Empty(local.Inputs);
        Assert.Empty(unc.Inputs);
    }

    [Fact]
    public void UnknownFeatureFallsBackWithIssue()
    {
        var route = _router.Route(ActivationEnvelope.Protocol("crosio://feature/not-real"));

        Assert.Equal(FeatureId.Home, route.Feature);
        Assert.Equal(ActivationIssue.UnknownFeature, route.Issue);
        Assert.True(route.ShouldShowMainWindow);
    }

    [Theory]
    [InlineData("capture-region", FeatureId.RegionScreenshot)]
    [InlineData("record-window", FeatureId.WindowRecording)]
    [InlineData("compress_images", FeatureId.ImageCompression)]
    public void FeatureCommandUsesStableRouteIdentifiers(string featureRoute, FeatureId expected)
    {
        var route = _router.Route(ActivationEnvelope.CommandLine(
            new[] { "--feature", featureRoute }));

        Assert.Equal(expected, route.Feature);
        Assert.Equal(ActivationIssue.None, route.Issue);
    }
}
