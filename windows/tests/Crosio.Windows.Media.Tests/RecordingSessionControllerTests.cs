using Crosio.Windows.Media;

namespace Crosio.Windows.Media.Tests;

public sealed class RecordingSessionControllerTests
{
    [Fact]
    public async Task StartAndStop_OnlyCompletesAfterContainerIsFinalizedAndPromoted()
    {
        var active = new FakeActiveRecording("/drafts/session.partial.mp4")
        {
            StopResult = new RecordingBackendResult(
                "/drafts/session.partial.mp4",
                TimeSpan.FromSeconds(8),
                123_456,
                ContainerFinalized: true),
        };
        var backend = new FakeBackend(AllSupported(), active);
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/Crosio Recording.mp4");
        await using var controller = new RecordingSessionController(backend, store);
        var phases = new List<RecordingSessionPhase>();
        controller.StateChanged += (_, snapshot) => phases.Add(snapshot.Phase);

        await controller.StartAsync(CreateRequest());
        var output = await controller.StopAsync();

        Assert.Equal("/completed/Crosio Recording.mp4", output);
        Assert.Equal(RecordingSessionPhase.Completed, controller.Snapshot.Phase);
        Assert.True(store.Promoted);
        Assert.Equal(
            [
                RecordingSessionPhase.CheckingCapabilities,
                RecordingSessionPhase.Starting,
                RecordingSessionPhase.Recording,
                RecordingSessionPhase.Stopping,
                RecordingSessionPhase.Completed,
            ],
            phases);
    }

    [Fact]
    public async Task Start_FailsBeforeBackendWhenRequestedAudioIsUnavailable()
    {
        var capabilities = AllSupported(
            new CapabilityStatus(
                RecordingCapability.SystemAudioLoopback,
                CapabilityAvailability.Unsupported,
                "wasapi-unavailable",
                "System audio is unavailable."));
        var backend = new FakeBackend(capabilities, new FakeActiveRecording("/drafts/session.partial.mp4"));
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4");
        await using var controller = new RecordingSessionController(backend, store);

        var exception = await Assert.ThrowsAsync<RecordingException>(() =>
            controller.StartAsync(CreateRequest(includeSystemAudio: true)));

        Assert.Equal(RecordingFailureCode.CapabilityUnavailable, exception.Code);
        Assert.Contains("wasapi-unavailable", exception.Message, StringComparison.Ordinal);
        Assert.False(backend.StartCalled);
        Assert.Equal(RecordingSessionPhase.Failed, controller.Snapshot.Phase);
    }

    [Fact]
    public async Task StartChecksDraftAndCompletedVolumesBeforeOpeningRecorder()
    {
        var draftDirectory = Path.Combine(Path.GetTempPath(), "Crosio-volume-test", "drafts");
        var draftPath = Path.Combine(draftDirectory, "session.partial.mp4");
        var active = new FakeActiveRecording(draftPath);
        var backend = new FakeBackend(AllSupported(), active);
        var store = new FakeFileStore(draftPath, "/completed/result.mp4")
        {
            VolumeIdentityProvider = path => path == draftDirectory
                ? "draft-volume"
                : "completed-volume",
            AvailableBytesProvider = path => path == draftDirectory
                ? 1
                : long.MaxValue,
        };
        await using var controller = new RecordingSessionController(backend, store);

        var exception = await Assert.ThrowsAsync<RecordingException>(() =>
            controller.StartAsync(CreateRequest()));

        Assert.Equal(RecordingFailureCode.InsufficientDiskSpace, exception.Code);
        Assert.False(backend.StartCalled);
        Assert.Equal(2, store.CheckedStorageLocations.Count);
    }

    [Fact]
    public async Task StartChecksSharedDraftAndCompletedVolumeOnlyOnce()
    {
        var active = new FakeActiveRecording("/drafts/session.partial.mp4");
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4")
        {
            VolumeIdentityProvider = _ => "shared-volume",
        };
        await using var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store);

        await controller.StartAsync(CreateRequest());

        Assert.Single(store.CheckedStorageLocations);
    }

    [Fact]
    public async Task Stop_RejectsUnfinalizedContainerAndDeletesDraft()
    {
        var active = new FakeActiveRecording("/drafts/session.partial.mp4")
        {
            StopResult = new RecordingBackendResult(
                "/drafts/session.partial.mp4",
                TimeSpan.FromSeconds(1),
                1_000,
                ContainerFinalized: false),
        };
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4");
        await using var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store);
        await controller.StartAsync(CreateRequest());

        var exception = await Assert.ThrowsAsync<RecordingException>(() => controller.StopAsync());

        Assert.Equal(RecordingFailureCode.FinalizationFailed, exception.Code);
        Assert.Equal(RecordingSessionPhase.Failed, controller.Snapshot.Phase);
        Assert.Contains("/drafts/session.partial.mp4", store.DeletedDrafts);
        Assert.False(store.Promoted);
    }

    [Fact]
    public async Task Stop_TimesOutWhenNativeCompletionNeverArrives()
    {
        var active = new NeverCompletesActiveRecording("/drafts/session.partial.mp4");
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4");
        var limits = RecordingSafetyLimits.Default with
        {
            StopFinalizationTimeout = TimeSpan.FromMilliseconds(50),
        };
        await using var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store,
            limits);
        await controller.StartAsync(CreateRequest());

        var exception = await Assert.ThrowsAsync<RecordingException>(() => controller.StopAsync());

        Assert.Equal(RecordingFailureCode.FinalizationFailed, exception.Code);
        Assert.Contains("recording-stop-timeout", exception.Message, StringComparison.Ordinal);
        Assert.Equal(RecordingSessionPhase.Failed, controller.Snapshot.Phase);
        Assert.Equal(1, active.StopCount);
        Assert.True(active.Disposed);
        Assert.Equal("/drafts/session.partial.mp4", controller.Snapshot.OutputPath);
        Assert.Empty(store.DeletedDrafts);
    }

    [Fact]
    public async Task PromotionFailure_PreservesFinalizedDraftForRecovery()
    {
        var draftPath = "/drafts/session.partial.mp4";
        var active = new FakeActiveRecording(draftPath)
        {
            StopResult = new RecordingBackendResult(
                draftPath,
                TimeSpan.FromSeconds(8),
                123_456,
                ContainerFinalized: true),
        };
        var store = new FakeFileStore(draftPath, "/completed/result.mp4")
        {
            PromotionFailure = new IOException("Destination is unavailable."),
        };
        await using var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store);
        await controller.StartAsync(CreateRequest());

        var exception = await Assert.ThrowsAsync<RecordingException>(() => controller.StopAsync());

        Assert.Equal(RecordingFailureCode.FinalizationFailed, exception.Code);
        Assert.Contains(draftPath, exception.Message, StringComparison.Ordinal);
        Assert.Equal(draftPath, controller.Snapshot.OutputPath);
        Assert.Empty(store.DeletedDrafts);
    }

    [Fact]
    public async Task StartingSecondSession_IsRejectedUntilResultIsReset()
    {
        var backend = new FakeBackend(
            AllSupported(),
            new FakeActiveRecording("/drafts/session.partial.mp4"));
        await using var controller = new RecordingSessionController(
            backend,
            new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4"));
        await controller.StartAsync(CreateRequest());

        var exception = await Assert.ThrowsAsync<RecordingException>(() =>
            controller.StartAsync(CreateRequest()));

        Assert.Equal(RecordingFailureCode.SessionAlreadyActive, exception.Code);
        Assert.Equal(1, backend.StartCount);
    }

    [Fact]
    public async Task CancelActiveRecording_CancelsBackendAndNeverPromotesDraft()
    {
        var active = new FakeActiveRecording("/drafts/session.partial.mp4");
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4");
        await using var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store);
        await controller.StartAsync(CreateRequest());

        await controller.CancelAsync();

        Assert.True(active.CancelCalled);
        Assert.Equal(RecordingSessionPhase.Cancelled, controller.Snapshot.Phase);
        Assert.Contains("/drafts/session.partial.mp4", store.DeletedDrafts);
        Assert.False(store.Promoted);
    }

    [Fact]
    public async Task CancelActiveRecording_WhenNativeCancelHangs_IsBoundedAndPreservesDraft()
    {
        var draftPath = "/drafts/session.partial.mp4";
        var active = new NeverCancelsActiveRecording(draftPath);
        var store = new FakeFileStore(draftPath, "/completed/result.mp4");
        var limits = RecordingSafetyLimits.Default with
        {
            StopFinalizationTimeout = TimeSpan.FromMilliseconds(50),
        };
        await using var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store,
            limits);
        await controller.StartAsync(CreateRequest());

        await controller.CancelAsync();

        Assert.Equal(RecordingSessionPhase.Cancelled, controller.Snapshot.Phase);
        Assert.Equal(draftPath, controller.Snapshot.OutputPath);
        Assert.True(active.Disposed);
        Assert.Empty(store.DeletedDrafts);
    }

    [Fact]
    public async Task CancelDuringBackendStart_DoesNotRaceDraftDeletionOrHangOnLateActiveSession()
    {
        var draftPath = "/drafts/session.partial.mp4";
        var backend = new DeferredBackend(AllSupported());
        var store = new FakeFileStore(draftPath, "/completed/result.mp4");
        var limits = RecordingSafetyLimits.Default with
        {
            StopFinalizationTimeout = TimeSpan.FromMilliseconds(50),
        };
        await using var controller = new RecordingSessionController(backend, store, limits);

        var start = controller.StartAsync(CreateRequest());
        await WaitUntilAsync(() => controller.Snapshot.Phase == RecordingSessionPhase.Starting);
        await controller.CancelAsync();
        Assert.Empty(store.DeletedDrafts);

        var active = new NeverCancelsActiveRecording(draftPath);
        backend.Complete(active);
        var exception = await Assert.ThrowsAsync<RecordingException>(() => start);

        Assert.Equal(RecordingFailureCode.StartCancelled, exception.Code);
        Assert.True(active.Disposed);
        Assert.Equal(draftPath, controller.Snapshot.OutputPath);
        Assert.Empty(store.DeletedDrafts);
    }

    [Fact]
    public async Task UncertainNativeStartCancellation_PreservesOwnedDraftAndCancelledState()
    {
        var draftPath = "/drafts/session.partial.mp4";
        var failure = new RecordingException(
            RecordingFailureCode.StartCancelled,
            $"Native cleanup is still in progress. The draft was preserved: {draftPath}",
            preserveDraft: true);
        var store = new FakeFileStore(draftPath, "/completed/result.mp4");
        await using var controller = new RecordingSessionController(
            new FailingStartBackend(AllSupported(), failure),
            store);

        var actual = await Assert.ThrowsAsync<RecordingException>(() =>
            controller.StartAsync(CreateRequest()));

        Assert.Same(failure, actual);
        Assert.Equal(RecordingSessionPhase.Cancelled, controller.Snapshot.Phase);
        Assert.Equal(draftPath, controller.Snapshot.OutputPath);
        Assert.Empty(store.DeletedDrafts);
    }

    [Fact]
    public async Task StartFailure_WhenDraftDeletionThrows_PreservesPrimaryFailureAndExitsStartingState()
    {
        var draftPath = "/drafts/session.partial.mp4";
        var failure = new RecordingException(
            RecordingFailureCode.BackendFailed,
            "The native start failed.");
        var store = new FakeFileStore(draftPath, "/completed/result.mp4")
        {
            DeleteFailure = new IOException("The draft is still locked."),
        };
        await using var controller = new RecordingSessionController(
            new FailingStartBackend(AllSupported(), failure),
            store);

        var actual = await Assert.ThrowsAsync<RecordingException>(() =>
            controller.StartAsync(CreateRequest()));

        Assert.Same(failure, actual);
        Assert.Equal(RecordingSessionPhase.Failed, controller.Snapshot.Phase);
        Assert.Equal(draftPath, controller.Snapshot.OutputPath);
        Assert.Contains("草稿清理失败", controller.Snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartCancellation_WhenDraftDeletionThrows_PreservesCancellationAndExitsStartingState()
    {
        var draftPath = "/drafts/session.partial.mp4";
        var store = new FakeFileStore(draftPath, "/completed/result.mp4")
        {
            DeleteFailure = new IOException("The draft is still locked."),
        };
        await using var controller = new RecordingSessionController(
            new FailingStartBackend(AllSupported(), new OperationCanceledException()),
            store);

        var actual = await Assert.ThrowsAsync<RecordingException>(() =>
            controller.StartAsync(CreateRequest()));

        Assert.Equal(RecordingFailureCode.StartCancelled, actual.Code);
        Assert.Equal(RecordingSessionPhase.Cancelled, controller.Snapshot.Phase);
        Assert.Equal(draftPath, controller.Snapshot.OutputPath);
        Assert.Contains("草稿清理失败", controller.Snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeDuringBackendStart_IsBoundedAndLeavesDraftOwnedByStartOperation()
    {
        var draftPath = "/drafts/session.partial.mp4";
        var backend = new DeferredBackend(AllSupported());
        var store = new FakeFileStore(draftPath, "/completed/result.mp4");
        var limits = RecordingSafetyLimits.Default with
        {
            StopFinalizationTimeout = TimeSpan.FromMilliseconds(50),
        };
        var controller = new RecordingSessionController(backend, store, limits);
        _ = controller.StartAsync(CreateRequest());
        await WaitUntilAsync(() => controller.Snapshot.Phase == RecordingSessionPhase.Starting);

        var disposal = controller.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(disposal, completed);
        await disposal;
        Assert.Empty(store.DeletedDrafts);
    }

    [Fact]
    public async Task ConcurrentStops_ShareOneSafeFinalization()
    {
        var active = new FakeActiveRecording("/drafts/session.partial.mp4");
        var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4"));
        await using (controller)
        {
            await controller.StartAsync(CreateRequest());

            var first = controller.StopAsync();
            var second = controller.StopAsync();
            var outputs = await Task.WhenAll(first, second);

            Assert.Equal(["/completed/result.mp4", "/completed/result.mp4"], outputs);
            Assert.Equal(1, active.StopCount);
        }
    }

    [Fact]
    public async Task RuntimeLimit_AutomaticallyFinalizesAndPromotesRecording()
    {
        var active = new LimitAwareFakeActiveRecording("/drafts/session.partial.mp4");
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4");
        await using var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store);
        await controller.StartAsync(CreateRequest());

        active.SignalLimit(RecordingRuntimeLimit.DiskReserve);
        await WaitUntilAsync(() => controller.Snapshot.Phase == RecordingSessionPhase.Completed);

        Assert.True(store.Promoted);
        Assert.Equal(1, active.StopCount);
        Assert.Equal("/completed/result.mp4", controller.Snapshot.OutputPath);
    }

    [Fact]
    public async Task NativeSourceEnd_AutomaticallyFinalizesAndPromotesRecording()
    {
        var active = new EndAwareFakeActiveRecording("/drafts/session.partial.mp4");
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4");
        await using var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store);
        await controller.StartAsync(CreateRequest());

        active.SignalEnd();
        await WaitUntilAsync(() => controller.Snapshot.Phase == RecordingSessionPhase.Completed);

        Assert.True(store.Promoted);
        Assert.Equal(1, active.StopCount);
    }

    [Fact]
    public async Task DisposeWhileRecording_FinalizesInsteadOfDeletingValidDraft()
    {
        var active = new FakeActiveRecording("/drafts/session.partial.mp4");
        var store = new FakeFileStore("/drafts/session.partial.mp4", "/completed/result.mp4");
        var controller = new RecordingSessionController(
            new FakeBackend(AllSupported(), active),
            store);
        await controller.StartAsync(CreateRequest());

        await controller.DisposeAsync();

        Assert.True(store.Promoted);
        Assert.Equal(1, active.StopCount);
        Assert.False(active.CancelCalled);
        Assert.DoesNotContain("/drafts/session.partial.mp4", store.DeletedDrafts);
    }

    private static RecordingRequest CreateRequest(bool includeSystemAudio = false) => new(
        new DisplayRecordingTarget("DISPLAY1", (nint)1, new PixelSize(1920, 1080)),
        includeSystemAudio,
        includeCursor: true,
        "/completed");

    private static RecordingCapabilityReport AllSupported(params CapabilityStatus[] overrides)
    {
        var statuses = Enum.GetValues<RecordingCapability>()
            .ToDictionary(
                capability => capability,
                capability => new CapabilityStatus(
                    capability,
                    CapabilityAvailability.Supported,
                    "supported",
                    "supported"));
        foreach (var status in overrides)
        {
            statuses[status.Capability] = status;
        }

        return new RecordingCapabilityReport(statuses.Values);
    }

    private sealed class FakeBackend : IRecordingBackend
    {
        private readonly RecordingCapabilityReport _capabilities;
        private readonly IActiveRecording _active;

        public FakeBackend(RecordingCapabilityReport capabilities, IActiveRecording active)
        {
            _capabilities = capabilities;
            _active = active;
        }

        public bool StartCalled { get; private set; }

        public int StartCount { get; private set; }

        public ValueTask<RecordingCapabilityReport> CheckCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_capabilities);
        }

        public Task<IActiveRecording> StartAsync(
            PreparedRecordingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalled = true;
            StartCount++;
            return Task.FromResult(_active);
        }
    }

    private sealed class DeferredBackend : IRecordingBackend
    {
        private readonly RecordingCapabilityReport _capabilities;
        private readonly TaskCompletionSource<IActiveRecording> _active =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeferredBackend(RecordingCapabilityReport capabilities)
        {
            _capabilities = capabilities;
        }

        public ValueTask<RecordingCapabilityReport> CheckCapabilitiesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_capabilities);

        public Task<IActiveRecording> StartAsync(
            PreparedRecordingRequest request,
            CancellationToken cancellationToken = default) =>
            _active.Task;

        public void Complete(IActiveRecording active) => _active.TrySetResult(active);
    }

    private sealed class FailingStartBackend : IRecordingBackend
    {
        private readonly RecordingCapabilityReport _capabilities;
        private readonly Exception _failure;

        public FailingStartBackend(RecordingCapabilityReport capabilities, Exception failure)
        {
            _capabilities = capabilities;
            _failure = failure;
        }

        public ValueTask<RecordingCapabilityReport> CheckCapabilitiesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_capabilities);

        public Task<IActiveRecording> StartAsync(
            PreparedRecordingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IActiveRecording>(_failure);
    }

    private sealed class FakeActiveRecording : IActiveRecording
    {
        public FakeActiveRecording(string draftPath)
        {
            DraftPath = draftPath;
            StopResult = new RecordingBackendResult(
                draftPath,
                TimeSpan.FromSeconds(1),
                1_000,
                ContainerFinalized: true);
        }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public string DraftPath { get; }

        public RecordingBackendResult StopResult { get; set; }

        public bool CancelCalled { get; private set; }

        public int StopCount { get; private set; }

        public Task<RecordingBackendResult> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.FromResult(StopResult);
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancelCalled = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverCompletesActiveRecording : IActiveRecording
    {
        private readonly TaskCompletionSource<RecordingBackendResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NeverCompletesActiveRecording(string draftPath)
        {
            DraftPath = draftPath;
        }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public string DraftPath { get; }

        public int StopCount { get; private set; }

        public bool Disposed { get; private set; }

        public Task<RecordingBackendResult> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return _completion.Task;
        }

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverCancelsActiveRecording : IActiveRecording
    {
        private readonly TaskCompletionSource<bool> _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NeverCancelsActiveRecording(string draftPath)
        {
            DraftPath = draftPath;
        }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public string DraftPath { get; }

        public bool Disposed { get; private set; }

        public Task<RecordingBackendResult> StopAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(CancellationToken cancellationToken = default) => _cancelled.Task;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LimitAwareFakeActiveRecording : IActiveRecording, IRecordingRuntimeLimitSource
    {
        private readonly TaskCompletionSource<RecordingRuntimeLimit> _limit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LimitAwareFakeActiveRecording(string draftPath)
        {
            DraftPath = draftPath;
        }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public string DraftPath { get; }

        public Task<RecordingRuntimeLimit> RuntimeLimitReached => _limit.Task;

        public int StopCount { get; private set; }

        public void SignalLimit(RecordingRuntimeLimit limit) => _limit.TrySetResult(limit);

        public Task<RecordingBackendResult> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.FromResult(new RecordingBackendResult(
                DraftPath,
                TimeSpan.FromSeconds(2),
                5_000,
                ContainerFinalized: true));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EndAwareFakeActiveRecording : IActiveRecording, IRecordingRuntimeEndSource
    {
        private readonly TaskCompletionSource<bool> _ended =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EndAwareFakeActiveRecording(string draftPath)
        {
            DraftPath = draftPath;
        }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public string DraftPath { get; }

        public Task RuntimeEnded => _ended.Task;

        public int StopCount { get; private set; }

        public void SignalEnd() => _ended.TrySetResult(true);

        public Task<RecordingBackendResult> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.FromResult(new RecordingBackendResult(
                DraftPath,
                TimeSpan.FromSeconds(2),
                5_000,
                ContainerFinalized: true));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeFileStore : IRecordingFileStore
    {
        private readonly string _draftPath;
        private readonly string _outputPath;

        public FakeFileStore(string draftPath, string outputPath)
        {
            _draftPath = draftPath;
            _outputPath = outputPath;
        }

        public bool Promoted { get; private set; }

        public Exception? PromotionFailure { get; init; }

        public Exception? DeleteFailure { get; init; }

        public Func<string, string>? VolumeIdentityProvider { get; init; }

        public Func<string, long>? AvailableBytesProvider { get; init; }

        public List<string> CheckedStorageLocations { get; } = [];

        public List<string> DeletedDrafts { get; } = [];

        public string GetVolumeIdentity(string path) =>
            VolumeIdentityProvider?.Invoke(path) ?? Path.GetPathRoot(Path.GetFullPath(path))!;

        public long GetAvailableBytes(string completedDirectory)
        {
            CheckedStorageLocations.Add(completedDirectory);
            return AvailableBytesProvider?.Invoke(completedDirectory) ?? long.MaxValue;
        }

        public string CreateDraftPath(Guid sessionId, RecordingEncodingProfile profile) => _draftPath;

        public string PromoteFinalizedDraft(
            string draftPath,
            string completedDirectory,
            string suggestedBaseName,
            RecordingEncodingProfile profile)
        {
            Promoted = true;
            if (PromotionFailure is not null)
            {
                throw PromotionFailure;
            }

            return _outputPath;
        }

        public void DeleteDraftIfPresent(string draftPath)
        {
            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }

            DeletedDrafts.Add(draftPath);
        }
    }
}
