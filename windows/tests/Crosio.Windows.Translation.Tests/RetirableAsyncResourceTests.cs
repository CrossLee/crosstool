using Xunit;

namespace Crosio.Windows.Translation.Tests;

public sealed class RetirableAsyncResourceTests
{
    [Fact]
    public async Task RetireDefersNativeResourceDisposalUntilEveryLeaseExits()
    {
        var resource = new TestResource();
        var entry = new RetirableAsyncResource<TestResource>(() => Task.FromResult(resource));
        Assert.True(entry.TryAcquire(out var first));
        Assert.True(entry.TryAcquire(out var second));
        Assert.Same(resource, await entry.GetValueAsync(CancellationToken.None));

        var retired = entry.RetireAsync();

        Assert.False(entry.TryAcquire(out _));
        Assert.False(retired.IsCompleted);
        first!.Dispose();
        Assert.False(retired.IsCompleted);
        second!.Dispose();
        await retired.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task RetireWaitsForAnInflightFactoryBeforeDisposingItsResult()
    {
        var source = new TaskCompletionSource<TestResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new RetirableAsyncResource<TestResource>(() => source.Task);
        Assert.True(entry.TryAcquire(out var lease));
        var loading = entry.GetValueAsync(CancellationToken.None);

        var retired = entry.RetireAsync();
        lease!.Dispose();
        Assert.False(retired.IsCompleted);

        var resource = new TestResource();
        source.SetResult(resource);
        Assert.Same(resource, await loading);
        await retired.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, resource.DisposeCount);
    }

    private sealed class TestResource : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
