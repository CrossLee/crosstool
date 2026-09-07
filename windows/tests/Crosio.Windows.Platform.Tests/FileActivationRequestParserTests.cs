using Crosio.Windows.Platform.Shell;

namespace Crosio.Windows.Platform.Tests;

public sealed class FileActivationRequestParserTests
{
    [Fact]
    public void EmptyLaunchShowsMainWindow()
    {
        var request = FileActivationRequestParser.Parse([]);

        Assert.Equal(FileActivationOperation.ShowMainWindow, request.Operation);
        Assert.True(request.ShouldShowMainWindow);
    }

    [Fact]
    public void DirectImagePathIsSilentCompressionActivation()
    {
        var request = FileActivationRequestParser.Parse([@"C:\Pictures\photo.jpg"]);

        Assert.Equal(FileActivationOperation.CompressImages, request.Operation);
        Assert.False(request.ShouldShowMainWindow);
        Assert.Equal(@"C:\Pictures\photo.jpg", Assert.Single(request.Paths));
    }

    [Fact]
    public void CopyPathsSwitchKeepsAllSelectedItemsAndNeverShowsWindow()
    {
        var request = FileActivationRequestParser.Parse(
        [
            FileActivationRequestParser.CopyPathsSwitch,
            @"C:\Course\notes.txt",
            @"C:\Course\Assets",
        ]);

        Assert.Equal(FileActivationOperation.CopyPaths, request.Operation);
        Assert.False(request.ShouldShowMainWindow);
        Assert.Equal(2, request.Paths.Count);
    }

    [Fact]
    public void StartupActivationIgnoresUnexpectedPaths()
    {
        var request = FileActivationRequestParser.Parse(
        [
            FileActivationRequestParser.BackgroundSwitch,
            @"C:\Pictures\photo.jpg",
        ]);

        Assert.Equal(FileActivationOperation.BackgroundOnly, request.Operation);
        Assert.Empty(request.Paths);
        Assert.Contains(@"C:\Pictures\photo.jpg", request.IgnoredArguments);
    }

    [Fact]
    public void ClipboardTextUsesExactPathsAndWindowsLineEndings()
    {
        var formatted = ClipboardPathFormatter.Format(
        [
            @"C:\Course\notes.txt",
            @"D:\素材\图片.png",
        ]);

        Assert.Equal("C:\\Course\\notes.txt\r\nD:\\素材\\图片.png", formatted);
    }
}
