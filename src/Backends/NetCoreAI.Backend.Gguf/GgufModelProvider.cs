using System.Collections.Concurrent;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Backends.Gguf;

/// <summary>Weights plus the context parameters they were loaded with; stored as the handle on <see cref="LoadedModel"/>.</summary>
internal sealed class GgufLoadedModel(ModelDescriptor descriptor, LLamaWeights weights, ModelParams parameters, GgufMetadata? metadata) : IDisposable
{
    private LLamaEmbedder? _embedder;

    // Every StatelessExecutor allocates an LLamaContext (and its multi-hundred-megabyte compute buffer)
    // in its constructor, and the type is not disposable, so one per request would leak. Executors are
    // pooled instead: a request rents one, returns it, and unloading disposes every context.
    private readonly ConcurrentBag<StatelessExecutor> _executors = [];
    private readonly List<StatelessExecutor> _allExecutors = [];
    private readonly Lock _executorLock = new();
    private bool _disposed;

    public ModelDescriptor Descriptor { get; } = descriptor;

    public LLamaWeights Weights { get; } = weights;

    public ModelParams Parameters { get; } = parameters;

    public IContextParams ContextParams => Parameters;

    public GgufMetadata? Metadata { get; } = metadata;

    /// <summary>The chat template: an explicit override on the model, else the one baked into the file.</summary>
    public LLamaTemplate? CreateTemplate()
    {
        try
        {
            return Descriptor.ChatTemplate is { Length: > 0 } custom
                ? new LLamaTemplate(custom)
                : new LLamaTemplate(Weights, strict: true);
        }
        catch (Exception)
        {
            // strict: true throws when the file carries no template at all.
            return null;
        }
    }

    /// <summary>
    /// The model's embedder, created once and shared.
    /// </summary>
    /// <remarks>
    /// Every LLamaEmbedder allocates its own llama.cpp context with a compute buffer of hundreds of
    /// megabytes, so creating one per call would exhaust memory partway through indexing a corpus —
    /// and the consumer pipeline is rebuilt per call, which would multiply them.
    /// </remarks>
    public LLamaEmbedder GetEmbedder(ILogger logger)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_executorLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _embedder ??= new LLamaEmbedder(Weights, Parameters, logger);
        }
    }

    /// <summary>Takes an executor from the pool, creating one only when every existing executor is busy.</summary>
    public StatelessExecutor RentExecutor(ILogger logger)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_executors.TryTake(out var pooled))
        {
            return pooled;
        }

        var executor = new StatelessExecutor(Weights, Parameters, logger) { ApplyTemplate = false };
        lock (_executorLock)
        {
            _allExecutors.Add(executor);
        }

        return executor;
    }

    /// <summary>How many executors (and therefore llama.cpp contexts) this model has created.</summary>
    public int ExecutorCount
    {
        get
        {
            lock (_executorLock)
            {
                return _allExecutors.Count;
            }
        }
    }

    public void ReturnExecutor(StatelessExecutor executor)
    {
        if (!_disposed)
        {
            _executors.Add(executor);
        }
    }

    public void Dispose()
    {
        _disposed = true;

        lock (_executorLock)
        {
            _embedder?.Dispose();
            _embedder = null;
        }

        lock (_executorLock)
        {
            foreach (var executor in _allExecutors)
            {
                // StatelessExecutor is not disposable, but the context it created in its constructor is.
                executor.Context.Dispose();
            }

            _allExecutors.Clear();
        }

        _executors.Clear();
        Weights.Dispose();
    }
}

/// <summary>
/// Adapts a loaded model's <see cref="LLamaEmbedder"/> to <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
/// </summary>
/// <remarks>
/// LLamaSharp 0.27 ships its own IEmbeddingGenerator implementation on LLamaEmbedder, but it disposes the
/// context it is using: the very first GenerateAsync call on a fresh embedder throws ObjectDisposedException,
/// and everything after it fails too. GetEmbeddings is reusable, so the adaptation is done here instead.
/// Revisit when LLamaSharp fixes its adapter.
///
/// Dispose deliberately does nothing: consumer pipelines are rebuilt per call and dispose what they wrap,
/// which would otherwise take the model's shared llama.cpp context with them. The embedder belongs to the
/// loaded model and is released when the model unloads.
/// </remarks>
internal sealed class SharedEmbeddingGenerator(LLamaEmbedder inner, string modelId, int? dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingGeneratorMetadata _metadata = new("gguf", null, modelId, dimensions);

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var embeddings = new List<Embedding<float>>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pooling is configured on the context, so one input yields one vector for the whole text.
            var vectors = await inner.GetEmbeddings(value ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (vectors.Count == 0)
            {
                throw new NetCoreAIException($"'{modelId}' returned no embedding for a {value?.Length ?? 0}-character input.");
            }

            embeddings.Add(new Embedding<float>(vectors[0]) { ModelId = modelId });
        }

        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        // Answered here rather than delegated: asking the LLamaEmbedder is what throws once its context
        // has been used.
        return serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
            : serviceType == typeof(LLamaEmbedder) ? inner
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Runs GGUF models in-process with llama.cpp through LLamaSharp. Reads the GGUF header to answer
/// capability and memory questions without loading weights.
/// </summary>
public sealed class GgufModelProvider(IOptionsMonitor<NetCoreAIOptions> netCoreAIOptions, ILoggerFactory loggerFactory) : IModelProvider
{
    public const string ProviderId = "gguf";

    private readonly ILogger<GgufModelProvider> _logger = loggerFactory.CreateLogger<GgufModelProvider>();
    private readonly ConcurrentDictionary<string, GgufMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);

    public string Id => ProviderId;

    public string DisplayName => "GGUF (llama.cpp)";

    public ProviderKind Kind => ProviderKind.Local;

    public IReadOnlyList<ModelFormat> SupportedFormats { get; } = [ModelFormat.Gguf];

    public bool CanLoad(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Format == ModelFormat.Gguf && model.Path is not null;
    }

    public ModelCapabilities GetCapabilities(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Capabilities != ModelCapabilities.None)
        {
            return model.Capabilities;
        }

        return TryReadMetadata(model)?.ToCapabilities()
            ?? new ModelCapabilities(ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.JsonMode | ModelCapability.StructuredOutput, model.ContextLength);
    }

    public ValueTask<MemoryEstimate> EstimateMemoryAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        var metadata = TryReadMetadata(model);
        var weightBytes = FileSize(model) ?? model.SizeBytes ?? 0;
        if (weightBytes == 0)
        {
            return ValueTask.FromResult(MemoryEstimate.Unknown);
        }

        var context = options.ContextSize ?? model.ContextLength ?? metadata?.ContextLength ?? netCoreAIOptions.CurrentValue.Models.DefaultContextSize;
        var kv = EstimateKvCacheBytes(metadata, context, options.KvCacheType);

        // llama.cpp needs the compute buffers and the model graph on top of weights and cache.
        const double overhead = 1.05;
        var total = (long)((weightBytes + kv) * overhead);

        var layers = metadata?.BlockCount ?? 32;
        var gpuLayers = options.GpuLayers ?? 0;
        var onGpuFraction = gpuLayers switch
        {
            -1 => 1.0,
            0 => 0.0,
            _ => Math.Clamp((double)gpuLayers / Math.Max(1, layers), 0, 1),
        };

        var vram = (long)(total * onGpuFraction);
        var explanation = metadata is null
            ? $"About {total / 1_048_576} MB for a {context}-token context (header not readable, so the KV cache is estimated)."
            : $"About {weightBytes / 1_048_576} MB of weights plus {kv / 1_048_576} MB of KV cache for {context} tokens across {layers} layers.";

        return ValueTask.FromResult(new MemoryEstimate(total - vram, vram, FitVerdict.Unknown, explanation));
    }

    /// <summary>KV cache size: 2 (K and V) x layers x kv-heads x head-dim x context x bytes-per-element.</summary>
    internal static long EstimateKvCacheBytes(GgufMetadata? metadata, int contextSize, KvCacheType kvCacheType)
    {
        var bytesPerElement = kvCacheType switch
        {
            KvCacheType.Q8 => 1.0625,   // q8_0 carries a scale per 32-value block
            KvCacheType.Q4 => 0.5625,   // q4_0 likewise
            _ => 2.0,                    // f16
        };

        if (metadata?.BlockCount is not { } layers || metadata.HeadDimension is not { } headDim || metadata.HeadCountKv is not { } kvHeads)
        {
            // No architecture detail: fall back to a per-token figure typical of a 7B model.
            return (long)(contextSize * 512L * bytesPerElement / 2.0);
        }

        return (long)(2L * layers * kvHeads * headDim * contextSize * bytesPerElement);
    }

    public async ValueTask<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        var path = ResolvePath(model) ?? throw new NetCoreAIException($"Model '{model.Name}' has no file path; re-import it or download it again.");
        if (!File.Exists(path))
        {
            throw new NetCoreAIException($"GGUF file for '{model.Name}' is missing at {path}. Re-download the model or fix its path in the registry.");
        }

        var settings = netCoreAIOptions.CurrentValue.Models;
        GgufNativeBackend.EnsureConfigured(
            options.ExecutionProvider == ExecutionProvider.Auto ? settings.ExecutionProvider : options.ExecutionProvider,
            _logger);

        var metadata = TryReadMetadata(model);
        var contextSize = options.ContextSize ?? Math.Min(model.ContextLength ?? int.MaxValue, settings.DefaultContextSize);
        if (metadata?.ContextLength is { } trained && contextSize > trained)
        {
            _logger.LogInformation("Requested context {Requested} exceeds the {ModelId} training length {Trained}; using {Trained}.", contextSize, model.Id, trained, trained);
            contextSize = trained;
        }

        var parameters = new ModelParams(path)
        {
            ContextSize = (uint)Math.Max(256, contextSize),
            GpuLayerCount = options.GpuLayers ?? 0,
            Threads = options.Threads ?? settings.Threads,
            Embeddings = metadata?.IsEmbeddingModel ?? false,
        };

        if (options.BatchSize is { } batch and > 0)
        {
            parameters.BatchSize = (uint)batch;
            parameters.UBatchSize = (uint)Math.Min(batch, 512);
        }

        if (options.FlashAttention is { } flash)
        {
            parameters.FlashAttention = flash;
        }

        if (ToGgmlType(options.KvCacheType) is { } kvType)
        {
            parameters.TypeK = kvType;
            parameters.TypeV = kvType;
        }

        if (parameters.Embeddings)
        {
            parameters.PoolingType = LLamaPoolingType.Mean;
        }

        _logger.LogInformation(
            "Loading GGUF model {ModelId} from {Path} (context {Context}, {GpuLayers} GPU layers)",
            model.Id, path, parameters.ContextSize, parameters.GpuLayerCount);

        LLamaWeights weights;
        try
        {
            weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken, progressReporter: null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NetCoreAIException)
        {
            throw new NetCoreAIException($"llama.cpp could not load '{model.Name}': {ex.Message}", ex);
        }

        var handle = new GgufLoadedModel(model, weights, parameters, metadata);
        var used = (long)weights.SizeInBytes + EstimateKvCacheBytes(metadata, (int)parameters.ContextSize.Value, options.KvCacheType);
        return new LoadedModel(model, Id, handle, used, options);
    }

    public ValueTask UnloadAsync(LoadedModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        (model.Handle as GgufLoadedModel)?.Dispose();
        return ValueTask.CompletedTask;
    }

    public IChatClient CreateChatClient(LoadedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var handle = Handle(model);
        if (handle.Metadata?.IsEmbeddingModel == true)
        {
            throw new NotSupportedException($"'{model.Descriptor.Name}' is an embedding model and cannot generate chat responses.");
        }

        return new GgufChatClient(handle, loggerFactory.CreateLogger<GgufChatClient>());
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var handle = Handle(model);
        if (!handle.Parameters.Embeddings)
        {
            throw new NotSupportedException(
                $"'{model.Descriptor.Name}' was loaded for generation, not embeddings. Register it as an embedding model (its GGUF header must declare a pooling type) and reload it.");
        }

        return new SharedEmbeddingGenerator(
            handle.GetEmbedder(loggerFactory.CreateLogger<GgufModelProvider>()),
            model.Descriptor.Id,
            handle.Metadata?.EmbeddingLength);
    }

    /// <summary>Test hook: the pooled-executor handle behind a loaded model.</summary>
    internal static GgufLoadedModel HandleOf(LoadedModel model) => Handle(model);

    private static GgufLoadedModel Handle(LoadedModel model) =>
        model.Handle as GgufLoadedModel ?? throw new InvalidOperationException($"Model '{model.Descriptor.Id}' was not loaded by the GGUF provider.");

    private static GGMLType? ToGgmlType(KvCacheType type) => type switch
    {
        KvCacheType.F16 => GGMLType.GGML_TYPE_F16,
        KvCacheType.Q8 => GGMLType.GGML_TYPE_Q8_0,
        KvCacheType.Q4 => GGMLType.GGML_TYPE_Q4_0,
        _ => null,
    };

    /// <summary>Reads and caches the GGUF header. Returns null when the file is absent or unreadable.</summary>
    internal GgufMetadata? TryReadMetadata(ModelDescriptor model)
    {
        var path = ResolvePath(model);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return _metadataCache.GetOrAdd(path, p =>
        {
            try
            {
                return GgufMetadataReader.Read(p);
            }
            catch (Exception ex) when (ex is GgufFormatException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the GGUF header of {Path}.", p);
                return null!;
            }
        });
    }

    private string? ResolvePath(ModelDescriptor model)
    {
        if (model.Path is not { Length: > 0 } path)
        {
            return null;
        }

        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(netCoreAIOptions.CurrentValue.DataDirectory, path));
    }

    private long? FileSize(ModelDescriptor model)
    {
        var path = ResolvePath(model);
        try
        {
            return path is not null && File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
