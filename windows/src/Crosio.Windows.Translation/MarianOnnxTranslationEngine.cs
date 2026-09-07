using Crosio.Windows.Intelligence.Translation;
using Microsoft.ML.OnnxRuntime;

namespace Crosio.Windows.Translation;

internal interface IMarianTranslationRuntime : IDisposable
{
    Task<string> TranslateAsync(string text, CancellationToken cancellationToken);
}

public sealed class MarianOnnxTranslationEngine : ITextTranslationEngine, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly OfflineModelCatalog _catalog;
    private readonly Func<InstalledTranslationModel, CancellationToken, Task<IMarianTranslationRuntime>> _runtimeFactory;
    private readonly Dictionary<string, RetirableAsyncResource<IMarianTranslationRuntime>> _runtimes =
        new(StringComparer.Ordinal);
    private readonly HashSet<RetirableAsyncResource<IMarianTranslationRuntime>> _lifetimeRuntimes = [];
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource? _operationsDrained;
    private Task? _disposeTask;
    private int _activeOperations;
    private bool _disposeStarted;

    public MarianOnnxTranslationEngine(OfflineModelCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtimeFactory = LoadRuntimeAsync;
    }

    internal MarianOnnxTranslationEngine(
        OfflineModelCatalog catalog,
        Func<InstalledTranslationModel, CancellationToken, Task<IMarianTranslationRuntime>> runtimeFactory)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
    }

    public async Task<string> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();

        CancellationTokenSource linkedCancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token);
            _activeOperations++;
        }
        try
        {
            var effectiveCancellation = linkedCancellation.Token;
            var active = await _catalog.ResolveActiveAsync(
                direction,
                verifyArtifactHashes: false,
                effectiveCancellation).ConfigureAwait(false);
            var cacheKey = $"{active.Manifest.Direction}:{active.ManifestSha256}";
            var (entry, lease) = AcquireRuntime(cacheKey, active);

            using (lease)
            {
                try
                {
                    var runtime = await entry.GetValueAsync(effectiveCancellation).ConfigureAwait(false);
                    return await runtime.TranslateAsync(text, effectiveCancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TranslationModelInvalidException)
                {
                    RetireRuntime(cacheKey, entry);
                    throw;
                }
                catch (Exception error) when (error is OnnxRuntimeException or IOException or UnauthorizedAccessException)
                {
                    RetireRuntime(cacheKey, entry);
                    throw new TranslationModelInvalidException("本地 Marian ONNX 翻译失败", error);
                }
            }
        }
        finally
        {
            linkedCancellation.Dispose();
            CompleteOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        RetirableAsyncResource<IMarianTranslationRuntime>[] runtimes;
        Task operationDrain;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }
            _disposeStarted = true;
            runtimes = [.. _lifetimeRuntimes];
            _runtimes.Clear();
            operationDrain = _activeOperations == 0
                ? Task.CompletedTask
                : (_operationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = DisposeCoreAsync(runtimes, operationDrain, completion);
        return new ValueTask(completion.Task);
    }

    private (RetirableAsyncResource<IMarianTranslationRuntime> Entry,
        RetirableAsyncResource<IMarianTranslationRuntime>.Lease Lease)
        AcquireRuntime(string cacheKey, InstalledTranslationModel active)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            while (true)
            {
                if (!_runtimes.TryGetValue(cacheKey, out var entry))
                {
                    entry = new RetirableAsyncResource<IMarianTranslationRuntime>(
                        () => _runtimeFactory(active, _shutdown.Token));
                    _runtimes.Add(cacheKey, entry);
                    _lifetimeRuntimes.Add(entry);
                }
                if (entry.TryAcquire(out var lease))
                {
                    return (entry, lease!);
                }
                _runtimes.Remove(cacheKey);
            }
        }
    }

    private void RetireRuntime(
        string cacheKey,
        RetirableAsyncResource<IMarianTranslationRuntime> entry)
    {
        lock (_gate)
        {
            if (_runtimes.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, entry))
            {
                _runtimes.Remove(cacheKey);
            }
        }
        var cleanup = entry.RetireAsync();
        _ = ForgetRetiredRuntimeAsync(entry, cleanup);
    }

    private async Task ForgetRetiredRuntimeAsync(
        RetirableAsyncResource<IMarianTranslationRuntime> entry,
        Task cleanup)
    {
        await cleanup.ConfigureAwait(false);
        lock (_gate)
        {
            _lifetimeRuntimes.Remove(entry);
        }
    }

    private async Task DisposeCoreAsync(
        IReadOnlyCollection<RetirableAsyncResource<IMarianTranslationRuntime>> runtimes,
        Task operationDrain,
        TaskCompletionSource completion)
    {
        Exception? disposalError = null;
        try
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (Exception error)
            {
                disposalError = error;
            }

            await Task.WhenAll(
                runtimes.Select(runtime => runtime.RetireAsync()).Append(operationDrain))
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            disposalError = disposalError is null
                ? error
                : new AggregateException(disposalError, error);
        }
        finally
        {
            _shutdown.Dispose();
            lock (_gate)
            {
                _lifetimeRuntimes.Clear();
            }
            if (disposalError is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(disposalError);
            }
        }
    }

    private void CompleteOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (_activeOperations <= 0)
            {
                throw new InvalidOperationException("翻译操作计数无效");
            }
            _activeOperations--;
            if (_disposeStarted && _activeOperations == 0)
            {
                drained = _operationsDrained;
            }
        }
        drained?.TrySetResult();
    }

    private async Task<IMarianTranslationRuntime> LoadRuntimeAsync(
        InstalledTranslationModel active,
        CancellationToken cancellationToken)
    {
        var verified = await _catalog.ValidatePackDirectoryAsync(
            active.DirectoryPath,
            active.ManifestSha256,
            verifyArtifactHashes: true,
            cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => new MarianRuntime(verified), cancellationToken).ConfigureAwait(false);
    }

    private sealed class MarianRuntime : IMarianTranslationRuntime
    {
        private readonly TranslationModelPackManifest _manifest;
        private readonly MarianSentencePieceCodec _codec;
        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private readonly SemaphoreSlim _inferenceGate = new(1, 1);

        public MarianRuntime(InstalledTranslationModel model)
        {
            _manifest = model.Manifest;
            _codec = MarianSentencePieceCodec.Load(model);
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };
            try
            {
                _encoder = new InferenceSession(
                    ModelPackPathPolicy.ResolveWithin(model.DirectoryPath, _manifest.Graph.EncoderModel),
                    options);
                _decoder = new InferenceSession(
                    ModelPackPathPolicy.ResolveWithin(model.DirectoryPath, _manifest.Graph.DecoderModel),
                    options);
                ValidateGraphContract();
            }
            catch
            {
                _encoder?.Dispose();
                _decoder?.Dispose();
                throw;
            }
            finally
            {
                options.Dispose();
            }
        }

        public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => TranslateCore(text, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _inferenceGate.Release();
            }
        }

        public void Dispose()
        {
            _decoder.Dispose();
            _encoder.Dispose();
            _inferenceGate.Dispose();
        }

        private string TranslateCore(string text, CancellationToken cancellationToken)
        {
            var sourceIds = _codec.Encode(text);
            var sourceMask = Enumerable.Repeat(1L, sourceIds.Length).ToArray();
            using var sourceIdsValue = OrtValue.CreateTensorValueFromMemory(
                sourceIds,
                [1, sourceIds.Length]);
            using var sourceMaskValue = OrtValue.CreateTensorValueFromMemory(
                sourceMask,
                [1, sourceMask.Length]);
            var encoderInputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
            {
                [_manifest.Graph.EncoderInputIds] = sourceIdsValue,
                [_manifest.Graph.EncoderAttentionMask] = sourceMaskValue,
            };
            using var encoderOutputs = RunCancelable(
                _encoder,
                encoderInputs,
                [_manifest.Graph.EncoderOutput],
                cancellationToken);
            var encoderHiddenState = encoderOutputs[0];

            var decoderIds = new List<long>(_manifest.Generation.MaxOutputTokens + 1)
            {
                _manifest.Generation.DecoderStartTokenId,
            };
            var outputIds = new List<int>(_manifest.Generation.MaxOutputTokens);
            for (var outputIndex = 0;
                 outputIndex < _manifest.Generation.MaxOutputTokens;
                 outputIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoderIdArray = decoderIds.ToArray();
                using var decoderIdsValue = OrtValue.CreateTensorValueFromMemory(
                    decoderIdArray,
                    [1, decoderIdArray.Length]);
                var decoderInputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
                {
                    [_manifest.Graph.DecoderInputIds] = decoderIdsValue,
                    [_manifest.Graph.DecoderAttentionMask] = sourceMaskValue,
                    [_manifest.Graph.DecoderEncoderHiddenStates] = encoderHiddenState,
                };
                using var decoderOutputs = RunCancelable(
                    _decoder,
                    decoderInputs,
                    [_manifest.Graph.DecoderLogitsOutput],
                    cancellationToken);
                var logitsValue = decoderOutputs[0];
                var shape = logitsValue.GetTensorTypeAndShape().Shape;
                if (shape.Length != 3 || shape[0] != 1 || shape[1] != decoderIdArray.Length || shape[2] <= 0)
                {
                    throw new TranslationModelInvalidException("Marian 解码器 logits 形状无效");
                }
                var vocabularySize = checked((int)shape[2]);
                var logits = logitsValue.GetTensorDataAsSpan<float>();
                var lastTokenLogits = logits.Slice(
                    checked((decoderIdArray.Length - 1) * vocabularySize),
                    vocabularySize);
                var nextToken = GreedyTokenSelector.Select(
                    lastTokenLogits,
                    outputIds.Count,
                    _manifest.Generation);
                if (nextToken == _manifest.Generation.EndOfSentenceTokenId)
                {
                    break;
                }
                outputIds.Add(nextToken);
                decoderIds.Add(nextToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (outputIds.Count == 0)
            {
                throw new TranslationModelInvalidException("Marian 模型没有生成翻译文字");
            }
            var output = _codec.Decode(outputIds);
            if (string.IsNullOrWhiteSpace(output))
            {
                throw new TranslationModelInvalidException("Marian 模型解码结果为空");
            }
            return _manifest.Direction == TranslationDirection.EnglishToChinese
                ? WindowsChineseScriptConverter.ToSimplified(output)
                : output;
        }

        private static IDisposableReadOnlyCollection<OrtValue> RunCancelable(
            InferenceSession session,
            IReadOnlyDictionary<string, OrtValue> inputs,
            IReadOnlyCollection<string> outputNames,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var options = new RunOptions();
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((RunOptions)state!).Terminate = true,
                options);
            try
            {
                return session.Run(options, inputs, outputNames);
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private void ValidateGraphContract()
        {
            RequireName(_encoder.InputNames, _manifest.Graph.EncoderInputIds, "编码器输入");
            RequireName(_encoder.InputNames, _manifest.Graph.EncoderAttentionMask, "编码器注意力输入");
            RequireName(_encoder.OutputNames, _manifest.Graph.EncoderOutput, "编码器输出");
            RequireName(_decoder.InputNames, _manifest.Graph.DecoderInputIds, "解码器输入");
            RequireName(_decoder.InputNames, _manifest.Graph.DecoderAttentionMask, "解码器注意力输入");
            RequireName(_decoder.InputNames, _manifest.Graph.DecoderEncoderHiddenStates, "解码器隐藏状态输入");
            RequireName(_decoder.OutputNames, _manifest.Graph.DecoderLogitsOutput, "解码器 logits 输出");
        }

        private static void RequireName(IReadOnlyCollection<string> names, string required, string description)
        {
            if (!names.Contains(required, StringComparer.Ordinal))
            {
                throw new TranslationModelInvalidException($"Marian ONNX 缺少{description} {required}");
            }
        }
    }
}
