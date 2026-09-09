namespace Crosio.Windows.Media;

public enum RecordingSessionPhase
{
    Idle,
    CheckingCapabilities,
    Starting,
    Recording,
    Stopping,
    Completed,
    Cancelled,
    Failed,
}

public sealed record RecordingSessionSnapshot(
    RecordingSessionPhase Phase,
    Guid? SessionId,
    RecordingTargetKind? TargetKind,
    DateTimeOffset? StartedAt,
    string? OutputPath,
    RecordingFailureCode? FailureCode,
    string? Message)
{
    public static RecordingSessionSnapshot Idle { get; } = new(
        RecordingSessionPhase.Idle,
        null,
        null,
        null,
        null,
        null,
        null);
}

/// <summary>
/// Owns the recording lifecycle and prevents capability probes, failed starts,
/// or unfinished containers from being reported as a successful recording.
/// UI and hotkey code can observe <see cref="Snapshot"/> without needing to
/// infer state from button labels.
/// </summary>
public sealed class RecordingSessionController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IRecordingBackend _backend;
    private readonly IRecordingFileStore _fileStore;
    private readonly RecordingSafetyLimits _limits;
    private RecordingSessionSnapshot _snapshot = RecordingSessionSnapshot.Idle;
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _runtimeLimitMonitorCancellation;
    private TaskCompletionSource<bool>? _startCompletion;
    private IActiveRecording? _activeRecording;
    private PreparedRecordingRequest? _preparedRequest;
    private Task<string>? _stopTask;
    private Task? _runtimeLimitMonitorTask;
    private Task? _runtimeEndMonitorTask;
    private int _generation;
    private bool _disposed;

    public RecordingSessionController(
        IRecordingBackend backend,
        IRecordingFileStore fileStore,
        RecordingSafetyLimits? limits = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _limits = limits ?? RecordingSafetyLimits.Default;
    }

    public event EventHandler<RecordingSessionSnapshot>? StateChanged;

    public RecordingSessionSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public async Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Guid sessionId;
        int generation;
        CancellationTokenSource linkedCancellation;
        TaskCompletionSource<bool> startCompletion;

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_snapshot.Phase != RecordingSessionPhase.Idle)
            {
                throw new RecordingException(
                    RecordingFailureCode.SessionAlreadyActive,
                    "Reset the current recording result before starting another recording.");
            }

            sessionId = Guid.NewGuid();
            generation = ++_generation;
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _startCancellation = linkedCancellation;
            _startCompletion = startCompletion;
            TransitionLocked(new RecordingSessionSnapshot(
                RecordingSessionPhase.CheckingCapabilities,
                sessionId,
                request.Target.Kind,
                null,
                null,
                null,
                "正在检查 Windows 录屏能力…"));
        }

        string? draftPath = null;
        var preserveCancelledDraft = false;
        var cancelledDraftMessage = string.Empty;
        try
        {
            var capabilities = await _backend
                .CheckCapabilitiesAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            var blockingIssues = capabilities.FindBlockingIssues(request);
            if (blockingIssues.Count > 0)
            {
                var explanation = string.Join(
                    " ",
                    blockingIssues.Select(issue => $"[{issue.Code}] {issue.Message}"));
                throw new RecordingException(
                    RecordingFailureCode.CapabilityUnavailable,
                    explanation);
            }

            linkedCancellation.Token.ThrowIfCancellationRequested();
            var profile = RecordingProfilePlanner.Create(
                request.Target.SourceSize,
                request.IncludeSystemAudio,
                _limits);
            draftPath = _fileStore.CreateDraftPath(sessionId, profile);
            var draftDirectory = Path.GetDirectoryName(draftPath)
                ?? throw new RecordingException(
                    RecordingFailureCode.InvalidRequest,
                    "The recording draft does not have a parent directory.");
            var checkedVolumes = new HashSet<string>(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            foreach (var storageLocation in new[] { request.CompletedDirectory, draftDirectory })
            {
                var volume = _fileStore.GetVolumeIdentity(storageLocation);
                if (!checkedVolumes.Add(volume))
                {
                    continue;
                }
                if (_fileStore.GetAvailableBytes(storageLocation) < _limits.RequiredFreeBytesBeforeStart)
                {
                    throw new RecordingException(
                        RecordingFailureCode.InsufficientDiskSpace,
                        "录屏保存目录或本地草稿所在磁盘至少需要 2 GiB 可用空间。");
                }
            }
            var prepared = new PreparedRecordingRequest(
                sessionId,
                request,
                profile,
                _limits,
                draftPath);

            lock (_sync)
            {
                EnsureCurrentStartLocked(generation, linkedCancellation.Token);
                _preparedRequest = prepared;
                TransitionLocked(_snapshot with
                {
                    Phase = RecordingSessionPhase.Starting,
                    Message = "正在启动屏幕录制…",
                });
            }

            var active = await _backend.StartAsync(prepared, linkedCancellation.Token).ConfigureAwait(false);
            var keepActive = false;
            lock (_sync)
            {
                if (generation == _generation && !linkedCancellation.IsCancellationRequested)
                {
                    _activeRecording = active;
                    keepActive = true;
                    TransitionLocked(_snapshot with
                    {
                        Phase = RecordingSessionPhase.Recording,
                        StartedAt = active.StartedAt,
                        Message = "正在录制",
                    });
                }
            }

            if (!keepActive)
            {
                var cancelCompleted = await CancelActiveSafelyAsync(
                        active,
                        _limits.StopFinalizationTimeout)
                    .ConfigureAwait(false);
                var disposeCompleted = await DisposeActiveSafelyAsync(
                        active,
                        _limits.StopFinalizationTimeout)
                    .ConfigureAwait(false);
                var finalizedDuringCancellation = Mp4ContainerInspector.IsFinalized(prepared.DraftPath);
                preserveCancelledDraft = finalizedDuringCancellation || !cancelCompleted || !disposeCompleted;
                if (preserveCancelledDraft)
                {
                    cancelledDraftMessage = finalizedDuringCancellation
                        ? $"录屏已在取消期间完成封装，文件已保留在：{prepared.DraftPath}"
                        : $"录屏取消未能安全完成。为避免丢失内容，草稿已保留在：{prepared.DraftPath}；该文件可能需要修复后播放。";
                }
                throw new OperationCanceledException(linkedCancellation.Token);
            }

            StartRuntimeMonitors(
                active,
                active as IRecordingRuntimeLimitSource,
                active as IRecordingRuntimeEndSource,
                generation);
        }
        catch (OperationCanceledException exception)
        {
            var deletionFailed = false;
            if (draftPath is not null && !preserveCancelledDraft)
            {
                deletionFailed = !TryDeleteDraftAfterStartFailure(draftPath);
            }

            var recoveryPath = preserveCancelledDraft || deletionFailed ? draftPath : null;
            var message = deletionFailed && draftPath is not null
                ? $"录屏已取消，但草稿清理失败，文件已保留在：{draftPath}"
                : "录屏已取消";
            TransitionAfterStartFailure(
                generation,
                RecordingSessionPhase.Cancelled,
                RecordingFailureCode.StartCancelled,
                message,
                recoveryPath);
            if (draftPath is not null && preserveCancelledDraft)
            {
                TransitionCancelledStartRecovery(sessionId, draftPath, cancelledDraftMessage);
            }
            throw new RecordingException(
                RecordingFailureCode.StartCancelled,
                "Recording was cancelled before it started.",
                exception);
        }
        catch (RecordingException exception)
        {
            var deletionFailed = false;
            if (draftPath is not null && !exception.PreserveDraft)
            {
                deletionFailed = !TryDeleteDraftAfterStartFailure(draftPath);
            }

            var recoveryPath = exception.PreserveDraft || deletionFailed ? draftPath : null;
            var message = deletionFailed && draftPath is not null
                ? $"{exception.Message} 草稿清理失败，文件已保留在：{draftPath}"
                : exception.Message;
            TransitionAfterStartFailure(
                generation,
                exception.Code == RecordingFailureCode.StartCancelled
                    ? RecordingSessionPhase.Cancelled
                    : RecordingSessionPhase.Failed,
                exception.Code,
                message,
                recoveryPath);
            throw;
        }
        catch (Exception exception)
        {
            var deletionFailed = false;
            if (draftPath is not null)
            {
                deletionFailed = !TryDeleteDraftAfterStartFailure(draftPath);
            }

            var wrapped = new RecordingException(
                RecordingFailureCode.BackendFailed,
                "The Windows recording backend could not start.",
                exception);
            TransitionAfterStartFailure(
                generation,
                RecordingSessionPhase.Failed,
                wrapped.Code,
                deletionFailed && draftPath is not null
                    ? $"{wrapped.Message} 草稿清理失败，文件已保留在：{draftPath}"
                    : wrapped.Message,
                deletionFailed ? draftPath : null);
            throw wrapped;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_startCancellation, linkedCancellation))
                {
                    _startCancellation = null;
                }
                if (ReferenceEquals(_startCompletion, startCompletion))
                {
                    _startCompletion = null;
                }
            }
            linkedCancellation.Dispose();
            startCompletion.TrySetResult(true);
        }
    }

    private void StartRuntimeMonitors(
        IActiveRecording active,
        IRecordingRuntimeLimitSource? runtimeLimitSource,
        IRecordingRuntimeEndSource? runtimeEndSource,
        int generation)
    {
        if (runtimeLimitSource is null && runtimeEndSource is null)
        {
            return;
        }

        lock (_sync)
        {
            if (generation != _generation ||
                !ReferenceEquals(_activeRecording, active) ||
                _snapshot.Phase != RecordingSessionPhase.Recording)
            {
                return;
            }

            _runtimeLimitMonitorCancellation?.Dispose();
            _runtimeLimitMonitorCancellation = new CancellationTokenSource();
            var cancellationToken = _runtimeLimitMonitorCancellation.Token;
            if (runtimeLimitSource is not null)
            {
                _runtimeLimitMonitorTask = MonitorRuntimeLimitAsync(
                    active,
                    runtimeLimitSource,
                    generation,
                    cancellationToken);
            }

            if (runtimeEndSource is not null)
            {
                _runtimeEndMonitorTask = MonitorRuntimeEndAsync(
                    active,
                    runtimeEndSource,
                    generation,
                    cancellationToken);
            }
        }
    }

    private async Task MonitorRuntimeLimitAsync(
        IActiveRecording active,
        IRecordingRuntimeLimitSource runtimeLimitSource,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var limit = await runtimeLimitSource.RuntimeLimitReached
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            Task<string>? stopTask = null;
            lock (_sync)
            {
                if (generation == _generation &&
                    ReferenceEquals(_activeRecording, active) &&
                    _snapshot.Phase == RecordingSessionPhase.Recording)
                {
                    var message = limit switch
                    {
                        RecordingRuntimeLimit.MaximumDuration => "已达到 2 小时上限，正在安全封装录屏…",
                        RecordingRuntimeLimit.MaximumFileSize => "已达到 10 GiB 上限，正在安全封装录屏…",
                        RecordingRuntimeLimit.DiskReserve => "磁盘空间接近安全下限，正在安全封装录屏…",
                        _ => "已达到录屏安全上限，正在安全封装录屏…",
                    };
                    stopTask = BeginStopLocked(message);
                }
            }

            if (stopTask is not null)
            {
                await stopTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // StopAndFinalizeAsync records any actionable failure in Snapshot.
            // This observer must never surface an unobserved task exception.
        }
    }

    private async Task MonitorRuntimeEndAsync(
        IActiveRecording active,
        IRecordingRuntimeEndSource runtimeEndSource,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await runtimeEndSource.RuntimeEnded
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            Task<string>? stopTask = null;
            lock (_sync)
            {
                if (generation == _generation &&
                    ReferenceEquals(_activeRecording, active) &&
                    _snapshot.Phase == RecordingSessionPhase.Recording)
                {
                    stopTask = BeginStopLocked("录制源已结束，正在安全封装录屏…");
                }
            }

            if (stopTask is not null)
            {
                await stopTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // StopAndFinalizeAsync owns the user-visible failure state.
        }
    }

    public Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        Task<string> stopTask;

        lock (_sync)
        {
            ThrowIfDisposed();
            stopTask = BeginStopLocked("正在停止并封装录屏…");
        }

        // Once MP4 finalization starts it must not be cancelled half-way. A
        // caller may cancel only its own wait; the underlying finalization and
        // safe promotion continue to completion.
        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    private Task<string> BeginStopLocked(string message)
    {
        if ((_snapshot.Phase is RecordingSessionPhase.Stopping or RecordingSessionPhase.Completed) &&
            _stopTask is not null)
        {
            return _stopTask;
        }

        if (_snapshot.Phase != RecordingSessionPhase.Recording ||
            _activeRecording is null ||
            _preparedRequest is null)
        {
            throw new RecordingException(
                RecordingFailureCode.SessionNotRecording,
                "There is no active recording to stop.");
        }

        var active = _activeRecording;
        var prepared = _preparedRequest;
        CancelRuntimeLimitMonitorLocked();
        TransitionLocked(_snapshot with
        {
            Phase = RecordingSessionPhase.Stopping,
            Message = message,
        });
        _stopTask = StopAndFinalizeAsync(active, prepared);
        return _stopTask;
    }

    private void CancelRuntimeLimitMonitorLocked()
    {
        _runtimeLimitMonitorCancellation?.Cancel();
        _runtimeLimitMonitorCancellation?.Dispose();
        _runtimeLimitMonitorCancellation = null;
    }

    private async Task<string> StopAndFinalizeAsync(
        IActiveRecording active,
        PreparedRecordingRequest prepared)
    {
        var deleteUnsafeDraftAfterDisposal = false;
        var backendFinalized = false;
        var preserveTimedOutDraft = false;
        var activeDisposed = false;
        try
        {
            RecordingBackendResult result;
            try
            {
                result = await active
                    .StopAsync(CancellationToken.None)
                    .WaitAsync(prepared.SafetyLimits.StopFinalizationTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                // StopAsync can time out just before the native completion
                // callback arrives. Dispose gives the recorder one final,
                // bounded opportunity to flush its container. Regardless of
                // the result, never delete this draft: a partial MP4 can still
                // contain recoverable user content.
                preserveTimedOutDraft = true;
                await DisposeActiveSafelyAsync(
                        active,
                        prepared.SafetyLimits.StopFinalizationTimeout)
                    .ConfigureAwait(false);
                activeDisposed = true;
                var finalizedAfterTimeout = Mp4ContainerInspector.IsFinalized(prepared.DraftPath);
                throw new RecordingException(
                    RecordingFailureCode.FinalizationFailed,
                    finalizedAfterTimeout
                        ? $"[recording-stop-timeout] 录屏停止超时，但已检测到完整 MP4。文件已保留在：{prepared.DraftPath}"
                        : $"[recording-stop-timeout] 录屏停止超时。为避免丢失内容，录制草稿已保留在：{prepared.DraftPath}；该文件可能需要修复后播放。",
                    exception);
            }

            if (!result.ContainerFinalized)
            {
                throw new RecordingException(
                    RecordingFailureCode.FinalizationFailed,
                    "The recorder stopped without finalizing the MP4 container.");
            }

            if (result.Duration <= TimeSpan.Zero || result.FileBytes <= 0)
            {
                throw new RecordingException(
                    RecordingFailureCode.FinalizationFailed,
                    "The finalized recording does not contain a non-empty timed media stream.");
            }

            if (!PathsEqual(result.DraftPath, prepared.DraftPath))
            {
                throw new RecordingException(
                    RecordingFailureCode.FinalizationFailed,
                    "The recorder returned a draft outside the active recording session.");
            }

            if (result.Duration > prepared.SafetyLimits.MaximumDuration ||
                result.FileBytes > prepared.SafetyLimits.MaximumFileBytes)
            {
                throw new RecordingException(
                    RecordingFailureCode.LimitReached,
                    "The finalized recording exceeds the configured duration or file-size limit.");
            }

            // From here on the draft is a validated, finalized MP4. If moving
            // it to the requested folder fails, preserving the draft is safer
            // than destroying the user's completed recording.
            backendFinalized = true;
            var outputPath = _fileStore.PromoteFinalizedDraft(
                prepared.DraftPath,
                prepared.Request.CompletedDirectory,
                prepared.Request.SuggestedBaseName,
                prepared.EncodingProfile);

            lock (_sync)
            {
                _activeRecording = null;
                _preparedRequest = null;
                TransitionLocked(_snapshot with
                {
                    Phase = RecordingSessionPhase.Completed,
                    OutputPath = outputPath,
                    FailureCode = null,
                    Message = "录屏已保存",
                });
            }

            return outputPath;
        }
        catch (RecordingException exception)
        {
            deleteUnsafeDraftAfterDisposal = !backendFinalized && !preserveTimedOutDraft;
            var surfaced = backendFinalized
                ? CreateRecoverablePromotionFailure(prepared.DraftPath, exception)
                : exception;
            lock (_sync)
            {
                _activeRecording = null;
                _preparedRequest = null;
                TransitionLocked(_snapshot with
                {
                    Phase = RecordingSessionPhase.Failed,
                    OutputPath = backendFinalized || preserveTimedOutDraft
                        ? prepared.DraftPath
                        : null,
                    FailureCode = surfaced.Code,
                    Message = surfaced.Message,
                });
            }

            if (ReferenceEquals(surfaced, exception))
            {
                throw;
            }

            throw surfaced;
        }
        catch (Exception exception)
        {
            deleteUnsafeDraftAfterDisposal = !backendFinalized;
            var wrapped = backendFinalized
                ? CreateRecoverablePromotionFailure(prepared.DraftPath, exception)
                : new RecordingException(
                    RecordingFailureCode.FinalizationFailed,
                    "The recording could not be finalized safely.",
                    exception);
            lock (_sync)
            {
                _activeRecording = null;
                _preparedRequest = null;
                TransitionLocked(_snapshot with
                {
                    Phase = RecordingSessionPhase.Failed,
                    OutputPath = backendFinalized ? prepared.DraftPath : null,
                    FailureCode = wrapped.Code,
                    Message = wrapped.Message,
                });
            }

            throw wrapped;
        }
        finally
        {
            if (!activeDisposed)
            {
                await DisposeActiveSafelyAsync(
                        active,
                        prepared.SafetyLimits.StopFinalizationTimeout)
                    .ConfigureAwait(false);
            }

            if (deleteUnsafeDraftAfterDisposal)
            {
                try
                {
                    _fileStore.DeleteDraftIfPresent(prepared.DraftPath);
                }
                catch
                {
                    // Keep the original finalization failure. A leftover draft
                    // is safer than masking the actionable recorder error.
                }
            }
        }
    }

    private static async Task<bool> CancelActiveSafelyAsync(
        IActiveRecording active,
        TimeSpan timeout)
    {
        Task cancellation;
        try
        {
            cancellation = active.CancelAsync(CancellationToken.None);
        }
        catch
        {
            return false;
        }

        try
        {
            await cancellation.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            ObserveLaterFault(cancellation);
            return false;
        }
    }

    private static async Task<bool> DisposeActiveSafelyAsync(
        IActiveRecording active,
        TimeSpan timeout)
    {
        try
        {
            var disposal = active.DisposeAsync().AsTask();
            try
            {
                await disposal.WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                // Observe a later native failure even though cleanup exceeded
                // its deadline. The controller must stay bounded.
                ObserveLaterFault(disposal);
                return false;
            }
        }
        catch
        {
            // The recording result is authoritative. Cleanup errors must not
            // hide it or turn a safely promoted file into a reported failure.
            return false;
        }
    }

    private static void ObserveLaterFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static RecordingException CreateRecoverablePromotionFailure(
        string draftPath,
        Exception exception) =>
        new(
            RecordingFailureCode.FinalizationFailed,
            $"录屏已经安全封装，但无法移动到录屏目录。原文件已保留在：{draftPath}",
            exception);

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? starting;
        IActiveRecording? active;
        string? draftPath;
        Task<string>? finalization;
        Guid? sessionId;

        lock (_sync)
        {
            ThrowIfDisposed();
            sessionId = _snapshot.SessionId;
            finalization = _snapshot.Phase == RecordingSessionPhase.Stopping ? _stopTask : null;
            if (finalization is not null)
            {
                starting = null;
                active = null;
                draftPath = null;
            }
            else
            {
                starting = _startCancellation;
                active = _activeRecording;
                draftPath = _preparedRequest?.DraftPath;

                if (starting is null && active is null)
                {
                    return;
                }

                starting?.Cancel();
                CancelRuntimeLimitMonitorLocked();
                _generation++;
                _activeRecording = null;
                _preparedRequest = null;
                TransitionLocked(_snapshot with
                {
                    Phase = RecordingSessionPhase.Cancelled,
                    FailureCode = RecordingFailureCode.StartCancelled,
                    Message = "录屏已取消",
                });

                // StartAsync owns the backend and draft until StartAsync
                // returns. Deleting here can race a native recorder that is
                // still opening the same file.
                if (starting is not null && active is null)
                {
                    draftPath = null;
                }
            }
        }

        if (finalization is not null)
        {
            await finalization.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (active is not null)
        {
            var cancelCompleted = await CancelActiveSafelyAsync(
                    active,
                    _limits.StopFinalizationTimeout)
                .ConfigureAwait(false);
            var disposeCompleted = await DisposeActiveSafelyAsync(
                    active,
                    _limits.StopFinalizationTimeout)
                .ConfigureAwait(false);
            var finalizedDuringCancellation = draftPath is not null &&
                Mp4ContainerInspector.IsFinalized(draftPath);
            if (!cancelCompleted || !disposeCompleted || finalizedDuringCancellation)
            {
                if (draftPath is not null && sessionId is not null)
                {
                    var message = finalizedDuringCancellation
                        ? $"录屏已在取消期间完成封装，文件已保留在：{draftPath}"
                        : $"录屏取消未能安全完成。为避免丢失内容，草稿已保留在：{draftPath}；该文件可能需要修复后播放。";
                    TransitionCancelledStartRecovery(sessionId.Value, draftPath, message);
                }
                draftPath = null;
            }
        }

        if (draftPath is not null)
        {
            _fileStore.DeleteDraftIfPresent(draftPath);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_snapshot.Phase is RecordingSessionPhase.CheckingCapabilities or
                RecordingSessionPhase.Starting or
                RecordingSessionPhase.Recording or
                RecordingSessionPhase.Stopping)
            {
                throw new RecordingException(
                    RecordingFailureCode.SessionAlreadyActive,
                    "An active recording cannot be reset.");
            }

            CancelRuntimeLimitMonitorLocked();
            _stopTask = null;
            _runtimeLimitMonitorTask = null;
            _runtimeEndMonitorTask = null;
            TransitionLocked(RecordingSessionSnapshot.Idle);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IActiveRecording? active;
        string? draftPath;
        Task<string>? gracefulFinalization;
        Task? pendingStart;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _startCancellation?.Cancel();
            if (_snapshot.Phase is RecordingSessionPhase.CheckingCapabilities or
                    RecordingSessionPhase.Starting &&
                _activeRecording is null &&
                _startCompletion is not null)
            {
                pendingStart = _startCompletion.Task;
                gracefulFinalization = null;
                active = null;
                draftPath = null;
                CancelRuntimeLimitMonitorLocked();
            }
            else if (_snapshot.Phase == RecordingSessionPhase.Recording &&
                _activeRecording is not null &&
                _preparedRequest is not null)
            {
                pendingStart = null;
                gracefulFinalization = BeginStopLocked("一爪正在退出，正在安全封装录屏…");
                active = null;
                draftPath = null;
            }
            else if (_snapshot.Phase == RecordingSessionPhase.Stopping && _stopTask is not null)
            {
                pendingStart = null;
                gracefulFinalization = _stopTask;
                active = null;
                draftPath = null;
            }
            else
            {
                pendingStart = null;
                gracefulFinalization = null;
                active = _activeRecording;
                draftPath = _preparedRequest?.DraftPath;
                _activeRecording = null;
                _preparedRequest = null;
                CancelRuntimeLimitMonitorLocked();
            }

            _disposed = true;
        }

        if (pendingStart is not null)
        {
            try
            {
                await pendingStart
                    .WaitAsync(_limits.StopFinalizationTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The backend ignored startup cancellation. Do not race it by
                // deleting the draft; process shutdown is now the final
                // containment boundary.
            }

            return;
        }

        if (gracefulFinalization is not null)
        {
            try
            {
                await gracefulFinalization.ConfigureAwait(false);
            }
            catch
            {
                // StopAndFinalizeAsync records the error and removes an unsafe
                // draft. Dispose cannot provide a second error channel.
            }

            return;
        }

        if (active is not null)
        {
            var cancelCompleted = await CancelActiveSafelyAsync(
                    active,
                    _limits.StopFinalizationTimeout)
                .ConfigureAwait(false);
            var disposeCompleted = await DisposeActiveSafelyAsync(
                    active,
                    _limits.StopFinalizationTimeout)
                .ConfigureAwait(false);
            if (!cancelCompleted || !disposeCompleted ||
                (draftPath is not null && Mp4ContainerInspector.IsFinalized(draftPath)))
            {
                // Shutdown has no UI channel left. Keeping the owned draft is
                // the only lossless outcome when native cancellation is
                // uncertain or produced a complete MP4.
                draftPath = null;
            }
        }

        if (draftPath is not null)
        {
            _fileStore.DeleteDraftIfPresent(draftPath);
        }

    }

    private void TransitionAfterStartFailure(
        int generation,
        RecordingSessionPhase phase,
        RecordingFailureCode failureCode,
        string message,
        string? outputPath = null)
    {
        lock (_sync)
        {
            if (generation != _generation)
            {
                return;
            }

            _activeRecording = null;
            _preparedRequest = null;
            TransitionLocked(_snapshot with
            {
                Phase = phase,
                OutputPath = outputPath,
                FailureCode = failureCode,
                Message = message,
            });
        }
    }

    private bool TryDeleteDraftAfterStartFailure(string draftPath)
    {
        try
        {
            _fileStore.DeleteDraftIfPresent(draftPath);
            return true;
        }
        catch
        {
            // Preserve the primary start/cancellation failure and leave the
            // controller in a terminal state. A locked draft is safer than an
            // exception that strands the UI in Starting.
            return false;
        }
    }

    private void TransitionCancelledStartRecovery(
        Guid sessionId,
        string draftPath,
        string message)
    {
        lock (_sync)
        {
            if (_snapshot.SessionId != sessionId ||
                _snapshot.Phase != RecordingSessionPhase.Cancelled)
            {
                return;
            }

            TransitionLocked(_snapshot with
            {
                OutputPath = draftPath,
                Message = message,
            });
        }
    }

    private void EnsureCurrentStartLocked(int generation, CancellationToken cancellationToken)
    {
        if (generation != _generation || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void TransitionLocked(RecordingSessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        var subscribers = StateChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<RecordingSessionSnapshot> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, snapshot);
            }
            catch
            {
                // A UI observer must not corrupt the recorder's state or turn
                // a successfully finalized file into a reported failure.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
