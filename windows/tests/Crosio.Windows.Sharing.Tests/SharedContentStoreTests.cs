using System.Text;
using Xunit;

namespace Crosio.Windows.Sharing.Tests;

public sealed class SharedContentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crosio-sharing-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReceivedFilesAreSanitizedAndNeverOverwriteExistingFiles()
    {
        var store = new SharedContentStore(_root);
        var first = await store.ReceiveFileAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("first")),
            "../课堂资料.txt",
            declaredLength: 5,
            remoteAddress: "127.0.0.1");
        var second = await store.ReceiveFileAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("second")),
            "课堂资料.txt",
            declaredLength: 6,
            remoteAddress: "127.0.0.1");

        Assert.Equal("课堂资料.txt", first.Title);
        Assert.Equal("课堂资料 2.txt", second.Title);
        Assert.Equal("first", await File.ReadAllTextAsync(first.FilePath!));
        Assert.Equal("second", await File.ReadAllTextAsync(second.FilePath!));
        Assert.Equal(2, store.PublicSnapshot().Count);
    }

    [Fact]
    public async Task ReceiveFileRejectsBytesBeyondHardLimitAndCleansTemporaryFile()
    {
        var store = new SharedContentStore(_root);
        await using var stream = new DeclaredLengthOnlyStream(SharedContentStore.MaximumUploadBytes + 1);

        var exception = await Assert.ThrowsAsync<SharedContentException>(() =>
            store.ReceiveFileAsync(
                stream,
                "too-large.bin",
                SharedContentStore.MaximumUploadBytes + 1,
                null));

        Assert.Contains("256 MB", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public void PublicListCombinesTeacherAndBrowserItemsButOwnerCanHideIncomingItem()
    {
        var store = new SharedContentStore(_root);
        var teacher = store.AddSharedText("老师发布");
        var browser = store.ReceiveText("学生发送", "192.168.1.20");

        Assert.Equal(2, store.PublicSnapshot().Count);
        store.HideIncomingFromPublic(browser.Id);

        var remaining = Assert.Single(store.PublicSnapshot());
        Assert.Equal(teacher.Id, remaining.Id);
    }

    [Fact]
    public void TextItemsRejectUtf8PayloadsBeyondThePublicLimit()
    {
        var store = new SharedContentStore(_root);
        var tooLarge = new string('中', SharedContentStore.MaximumTextBytes / 3 + 1);

        var exception = Assert.Throws<SharedContentException>(() => store.ReceiveText(tooLarge, null));

        Assert.Contains("64 KB", exception.Message, StringComparison.Ordinal);
        Assert.Empty(store.PublicSnapshot());
    }

    [Fact]
    public async Task ReceiveChecksTotalInboxQuotaBeforeReadingOrCreatingTemporaryFiles()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Path.Combine(_root, "existing.bin"), new byte[6]);
        var store = CreateStore(maximumUploadBytes: 100, maximumInboxBytes: 8, availableBytes: 100);
        await using var source = new CountingStream(new byte[3]);

        var exception = await Assert.ThrowsAsync<SharedContentException>(() =>
            store.ReceiveFileAsync(source, "new.bin", declaredLength: 3, remoteAddress: null));

        Assert.Equal(SharedContentErrorCode.InboxQuotaExceeded, exception.Code);
        Assert.Equal(0, source.ReadCount);
        Assert.Equal(["existing.bin"], Directory.EnumerateFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ReceivePreservesConfiguredDiskSafetyMarginBeforeReading()
    {
        var store = CreateStore(
            maximumUploadBytes: 100,
            maximumInboxBytes: 100,
            availableBytes: 12,
            minimumFreeDiskBytes: 10);
        await using var source = new CountingStream(new byte[3]);

        var exception = await Assert.ThrowsAsync<SharedContentException>(() =>
            store.ReceiveFileAsync(source, "new.bin", declaredLength: 3, remoteAddress: null));

        Assert.Equal(SharedContentErrorCode.InsufficientDiskSpace, exception.Code);
        Assert.Equal(0, source.ReadCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task StreamingQuotaCheckRejectsIncorrectDeclaredLengthAndCleansTemporaryFile()
    {
        var store = CreateStore(maximumUploadBytes: 100, maximumInboxBytes: 5, availableBytes: 100);

        var exception = await Assert.ThrowsAsync<SharedContentException>(() =>
            store.ReceiveFileAsync(
                new MemoryStream(new byte[6]),
                "dishonest.bin",
                declaredLength: 1,
                remoteAddress: null));

        Assert.Equal(SharedContentErrorCode.InboxQuotaExceeded, exception.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task ConcurrentReceivesReserveQuotaSerially()
    {
        var store = CreateStore(maximumUploadBytes: 100, maximumInboxBytes: 6, availableBytes: 100);

        var attempts = Enumerable.Range(0, 2).Select(async index =>
        {
            try
            {
                await store.ReceiveFileAsync(
                    new MemoryStream(new byte[4]),
                    $"upload-{index}.bin",
                    declaredLength: 4,
                    remoteAddress: null);
                return true;
            }
            catch (SharedContentException error) when (error.Code == SharedContentErrorCode.InboxQuotaExceeded)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(Directory.EnumerateFiles(_root));
        Assert.Equal(4, new FileInfo(Directory.EnumerateFiles(_root).Single()).Length);
    }

    [Fact]
    public async Task ConcurrentTextReceivesCannotBypassIncomingItemLimit()
    {
        var store = CreateStore(
            maximumUploadBytes: 100,
            maximumInboxBytes: 100,
            availableBytes: 100,
            maximumIncomingItems: 1,
            maximumIncomingTextBytes: 100);

        var attempts = Enumerable.Range(0, 2).Select(async index =>
        {
            try
            {
                await store.ReceiveTextAsync($"message-{index}", remoteAddress: null);
                return true;
            }
            catch (SharedContentException error)
                when (error.Code == SharedContentErrorCode.IncomingItemLimitExceeded)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(store.PublicSnapshot());
    }

    [Fact]
    public async Task IncomingTextUsesAggregateByteQuota()
    {
        var store = CreateStore(
            maximumUploadBytes: 100,
            maximumInboxBytes: 100,
            availableBytes: 100,
            maximumIncomingTextBytes: 5);
        await store.ReceiveTextAsync("1234", remoteAddress: null);

        var error = await Assert.ThrowsAsync<SharedContentException>(() =>
            store.ReceiveTextAsync("56", remoteAddress: null));

        Assert.Equal(SharedContentErrorCode.IncomingTextQuotaExceeded, error.Code);
        Assert.Single(store.PublicSnapshot());
    }

    [Fact]
    public async Task EmptyFilesAreRejectedForDeclaredAndObservedLengths()
    {
        var store = CreateStore(maximumUploadBytes: 100, maximumInboxBytes: 100, availableBytes: 100);
        await using var declaredEmpty = new CountingStream([]);

        var declaredError = await Assert.ThrowsAsync<SharedContentException>(() =>
            store.ReceiveFileAsync(declaredEmpty, "empty.bin", declaredLength: 0, remoteAddress: null));
        var observedError = await Assert.ThrowsAsync<SharedContentException>(() =>
            store.ReceiveFileAsync(
                new MemoryStream([]),
                "also-empty.bin",
                declaredLength: null,
                remoteAddress: null));

        Assert.Equal(SharedContentErrorCode.InvalidContent, declaredError.Code);
        Assert.Equal(SharedContentErrorCode.InvalidContent, observedError.Code);
        Assert.Equal(0, declaredEmpty.ReadCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task ClearPublicItemsDeletesInboxFilesButNeverOutgoingSourceFiles()
    {
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"crosio-outgoing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        var outgoingPath = Path.Combine(outsideDirectory, "teacher.txt");
        await File.WriteAllTextAsync(outgoingPath, "keep");
        try
        {
            var store = new SharedContentStore(_root);
            store.AddSharedFile(outgoingPath);
            var incoming = await store.ReceiveFileAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("delete")),
                "student.txt",
                declaredLength: 6,
                remoteAddress: null);

            await store.ClearPublicItemsAsync();

            Assert.False(File.Exists(incoming.FilePath));
            Assert.True(File.Exists(outgoingPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
            Assert.Empty(store.PublicSnapshot());
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ClearInboxRemovesLinkWithoutFollowingItOutsideInbox()
    {
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"crosio-link-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        var target = Path.Combine(outsideDirectory, "target.txt");
        await File.WriteAllTextAsync(target, "do not delete");
        var store = new SharedContentStore(_root);
        var outgoing = store.AddSharedText("keep sharing");
        var incoming = await store.ReceiveFileAsync(
            new MemoryStream([1]),
            "managed.bin",
            declaredLength: 1,
            remoteAddress: null);
        var link = Path.Combine(_root, "linked.txt");
        try
        {
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            await store.ClearInboxAsync();

            Assert.False(File.Exists(incoming.FilePath));
            Assert.False(File.Exists(link));
            Assert.True(File.Exists(target));
            Assert.Equal("do not delete", await File.ReadAllTextAsync(target));
            Assert.Equal(outgoing.Id, Assert.Single(store.PublicSnapshot()).Id);
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    private SharedContentStore CreateStore(
        long maximumUploadBytes,
        long maximumInboxBytes,
        long availableBytes,
        long minimumFreeDiskBytes = 0,
        int maximumIncomingItems = SharedContentStore.MaximumIncomingItems,
        long maximumIncomingTextBytes = SharedContentStore.MaximumIncomingTextBytes) =>
        new(
            _root,
            new SharedContentStoreOptions(
                MaximumUploadBytes: maximumUploadBytes,
                MaximumInboxBytes: maximumInboxBytes,
                MinimumFreeDiskBytes: minimumFreeDiskBytes,
                MaximumIncomingItems: maximumIncomingItems,
                MaximumIncomingTextBytes: maximumIncomingTextBytes),
            new FixedDiskSpaceProbe(availableBytes));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class DeclaredLengthOnlyStream(long length) : Stream
    {
        public override long Length => length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class FixedDiskSpaceProbe(long availableBytes) : IInboxDiskSpaceProbe
    {
        public long AvailableFreeBytes(string path) => availableBytes;
    }
}
