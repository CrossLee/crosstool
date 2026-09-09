using System.Diagnostics;

namespace Crosio.Windows.Media.Tests;

public sealed class DedicatedNativeCallWorkerTests
{
    [Fact]
    public async Task LifecycleCalls_ReturnBeforeSynchronouslyBlockingNativeMethods_AndStaySerialized()
    {
        using var native = new BlockingNativeLifecycle();
        using var session = new NativeRecordingSessionWorker("Crosio fake recording session");
        Task<int>? create = null;
        Task? start = null;
        Task? stop = null;
        Task? disposal = null;

        try
        {
            create = AssertReturnsImmediately(() => session.CreateAsync(native.Create));
            Assert.True(native.CreateEntered.Wait(TimeSpan.FromSeconds(1)));
            native.ReleaseCreate.Set();
            Assert.Equal(42, await create.WaitAsync(TimeSpan.FromSeconds(1)));

            start = AssertReturnsImmediately(() => session.StartAsync(native.Start));
            Assert.True(native.StartEntered.Wait(TimeSpan.FromSeconds(1)));
            Assert.True(native.RunsOnBackgroundThread);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(ApartmentState.MTA, native.ApartmentState);
            }

            stop = AssertReturnsImmediately(() => session.StopAsync(native.Stop));
            Assert.False(native.StopEntered.Wait(TimeSpan.FromMilliseconds(100)));

            native.ReleaseStart.Set();
            await start.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(native.StopEntered.Wait(TimeSpan.FromSeconds(1)));

            disposal = AssertReturnsImmediately(() => session.DisposeNativeAsync(native.DisposeNative));
            Assert.False(native.DisposeEntered.Wait(TimeSpan.FromMilliseconds(100)));

            native.ReleaseStop.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(native.DisposeEntered.Wait(TimeSpan.FromSeconds(1)));

            native.ReleaseDispose.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(["create", "start", "stop", "dispose"], native.Calls);
        }
        finally
        {
            native.ReleaseCreate.Set();
            native.ReleaseStart.Set();
            native.ReleaseStop.Set();
            native.ReleaseDispose.Set();
            await ObserveAsync(create);
            await ObserveAsync(start);
            await ObserveAsync(stop);
            await ObserveAsync(disposal);
        }
    }

    [Fact]
    public async Task Complete_DoesNotJoinAStuckNativeCall_AndWorkerCannotKeepProcessAlive()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var worker = new DedicatedNativeCallWorker("Crosio native worker test");

        var operation = worker.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
        });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        var stopwatch = Stopwatch.StartNew();
        worker.Complete();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        release.Set();
        await operation.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Complete_RacingInvokeRejectsNewWorkWithoutThrowingFromTheCaller()
    {
        using var worker = new DedicatedNativeCallWorker("Crosio enqueue race test");
        using var start = new ManualResetEventSlim();
        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                try
                {
                    await worker.InvokeAsync(static () => { });
                }
                catch (ObjectDisposedException)
                {
                }
            }))
            .ToArray();
        var completion = Task.Run(() =>
        {
            start.Wait();
            worker.Complete();
        });

        start.Set();
        await Task.WhenAll(callers.Append(completion)).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static Task AssertReturnsImmediately(Func<Task> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var task = operation();
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"The asynchronous entry point blocked its caller for {stopwatch.Elapsed}.");
        return task;
    }

    private static Task<T> AssertReturnsImmediately<T>(Func<Task<T>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var task = operation();
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"The asynchronous entry point blocked its caller for {stopwatch.Elapsed}.");
        return task;
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }
    }

    private sealed class BlockingNativeLifecycle : IDisposable
    {
        private readonly object _sync = new();

        public ManualResetEventSlim CreateEntered { get; } = new();

        public ManualResetEventSlim StartEntered { get; } = new();

        public ManualResetEventSlim StopEntered { get; } = new();

        public ManualResetEventSlim DisposeEntered { get; } = new();

        public ManualResetEventSlim ReleaseCreate { get; } = new();

        public ManualResetEventSlim ReleaseStart { get; } = new();

        public ManualResetEventSlim ReleaseStop { get; } = new();

        public ManualResetEventSlim ReleaseDispose { get; } = new();

        public List<string> Calls { get; } = [];

        public bool RunsOnBackgroundThread { get; private set; }

        public ApartmentState ApartmentState { get; private set; }

        public int Create()
        {
            Block("create", CreateEntered, ReleaseCreate);
            return 42;
        }

        public void Start() => Block("start", StartEntered, ReleaseStart);

        public void Stop() => Block("stop", StopEntered, ReleaseStop);

        public void DisposeNative() => Block("dispose", DisposeEntered, ReleaseDispose);

        public void Dispose()
        {
            CreateEntered.Dispose();
            StartEntered.Dispose();
            StopEntered.Dispose();
            DisposeEntered.Dispose();
            ReleaseCreate.Dispose();
            ReleaseStart.Dispose();
            ReleaseStop.Dispose();
            ReleaseDispose.Dispose();
        }

        private void Block(
            string operation,
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            lock (_sync)
            {
                Calls.Add(operation);
            }

            RunsOnBackgroundThread = Thread.CurrentThread.IsBackground;
            ApartmentState = Thread.CurrentThread.GetApartmentState();
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException($"The test did not release {operation}.");
            }
        }
    }
}
