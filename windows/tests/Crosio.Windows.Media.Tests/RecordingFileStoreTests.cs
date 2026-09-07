using Crosio.Windows.Media;

namespace Crosio.Windows.Media.Tests;

public sealed class RecordingFileStoreTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Crosio.Windows.Media.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PromoteFinalizedDraft_MovesOwnedDraftWithoutOverwriting()
    {
        var drafts = Path.Combine(_testRoot, "Drafts");
        var completed = Path.Combine(_testRoot, "Recordings");
        var store = new RecordingFileStore(drafts);
        var profile = RecordingProfilePlanner.Create(new PixelSize(640, 480), false);
        var draft = store.CreateDraftPath(Guid.NewGuid(), profile);
        Directory.CreateDirectory(Path.GetDirectoryName(draft)!);
        File.WriteAllBytes(draft, [1, 2, 3]);

        var output = store.PromoteFinalizedDraft(
            draft,
            completed,
            "Lesson: One",
            profile);

        Assert.False(File.Exists(draft));
        Assert.True(File.Exists(output));
        Assert.Equal(".mp4", Path.GetExtension(output));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(output));
    }

    [Fact]
    public void PromoteFinalizedDraft_RejectsPathOutsideOwnedDraftDirectory()
    {
        var drafts = Path.Combine(_testRoot, "Drafts");
        var outside = Path.Combine(_testRoot, "outside.mp4");
        Directory.CreateDirectory(_testRoot);
        File.WriteAllBytes(outside, [1]);
        var store = new RecordingFileStore(drafts);
        var profile = RecordingProfilePlanner.Create(new PixelSize(640, 480), false);

        var exception = Assert.Throws<RecordingException>(() =>
            store.PromoteFinalizedDraft(
                outside,
                Path.Combine(_testRoot, "Recordings"),
                "Recording",
                profile));

        Assert.Equal(RecordingFailureCode.FinalizationFailed, exception.Code);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void PromoteFinalizedDraft_RejectsEmptyDraft()
    {
        var drafts = Path.Combine(_testRoot, "Drafts");
        var store = new RecordingFileStore(drafts);
        var profile = RecordingProfilePlanner.Create(new PixelSize(640, 480), false);
        var draft = store.CreateDraftPath(Guid.NewGuid(), profile);
        Directory.CreateDirectory(Path.GetDirectoryName(draft)!);
        File.WriteAllBytes(draft, []);

        var exception = Assert.Throws<RecordingException>(() =>
            store.PromoteFinalizedDraft(
                draft,
                Path.Combine(_testRoot, "Recordings"),
                "Recording",
                profile));

        Assert.Equal(RecordingFailureCode.FinalizationFailed, exception.Code);
        Assert.True(File.Exists(draft));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
