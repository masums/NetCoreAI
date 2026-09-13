using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;
using GenAiTokenizer = Microsoft.ML.OnnxRuntimeGenAI.Tokenizer;
using Microsoft.ML.Tokenizers;

namespace NetCoreAI.Backends.Onnx;

/// <summary>A generative model held open by ONNX Runtime GenAI; stored as the handle on <see cref="LoadedModel"/>.</summary>
internal sealed class OnnxGenerativeModel(ModelDescriptor descriptor, OnnxModelFolder folder, Config config, Model model, GenAiTokenizer tokenizer, int contextSize) : IDisposable
{
    public ModelDescriptor Descriptor { get; } = descriptor;

    public OnnxModelFolder Folder { get; } = folder;

    public Model Model { get; } = model;

    public GenAiTokenizer Tokenizer { get; } = tokenizer;

    /// <summary>Total prompt + generation budget in tokens.</summary>
    public int ContextSize { get; } = contextSize;

    public void Dispose()
    {
        Tokenizer.Dispose();
        Model.Dispose();

        // The config owns the native provider list the model was built from; it outlives the model by design.
        config.Dispose();
    }
}

/// <summary>An encoder model held open by ONNX Runtime for embeddings.</summary>
internal sealed class OnnxEmbeddingModel(ModelDescriptor descriptor, OnnxModelFolder folder, InferenceSession session, BertTokenizer tokenizer) : IDisposable
{
    public ModelDescriptor Descriptor { get; } = descriptor;

    public OnnxModelFolder Folder { get; } = folder;

    public InferenceSession Session { get; } = session;

    public BertTokenizer Tokenizer { get; } = tokenizer;

    /// <summary>Input names the graph declares, so optional inputs are only fed when expected.</summary>
    public IReadOnlySet<string> InputNames { get; } = session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);

    public int? Dimensions { get; init; }

    /// <summary>Longest sequence fed to the encoder; longer input is truncated.</summary>
    public int MaxTokens { get; init; } = 512;

    public bool MeanPooling { get; init; } = true;

    public bool Normalize { get; init; } = true;

    public void Dispose() => Session.Dispose();
}

/// <summary>
/// Runs ONNX models in-process: generative models through ONNX Runtime GenAI (a folder with
/// genai_config.json) and sentence-transformers exports through ONNX Runtime with pooling.
/// Reads the folder configuration to answer capability and memory questions without loading weights.
/// </summary>
public sealed class OnnxModelProvider(IOptionsMonitor<NetCoreAIOptions> netCoreAIOptions, ILoggerFactory loggerFactory) : IModelProvider
{
    public const string ProviderId = "onnx";

    private readonly ILogger<OnnxModelProvider> _logger = loggerFactory.CreateLogger<OnnxModelProvider>();
    private readonly ConcurrentDictionary<string, OnnxModelFolder> _folderCache = new(StringComparer.OrdinalIgnoreCase);

    public string Id => ProviderId;

    public string DisplayName => "ONNX (ONNX Runtime)";

    public ProviderKind Kind => ProviderKind.Local;

    public IReadOnlyList<ModelFormat> SupportedFormats { get; } = [ModelFormat.Onnx];

    public bool CanLoad(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Format == ModelFormat.Onnx && model.Path is not null;
    }

    public ModelCapabilities GetCapabilities(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Capabilities != ModelCapabilities.None)
        {
            return model.Capabilities;
        }

        return TryReadFolder(model)?.ToCapabilities()
            ?? new ModelCapabilities(ModelCapability.Chat | ModelCapability.Streaming, model.ContextLength);
    }

    public ValueTask<MemoryEstimate> EstimateMemoryAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        var folder = TryReadFolder(model);
        var weightBytes = folder?.GraphBytes ?? model.SizeBytes ?? 0;
        if (weightBytes == 0)
        {
            return ValueTask.FromResult(MemoryEstimate.Unknown);
        }

        if (folder?.Kind == OnnxModelKind.Embedding)
        {
            // Encoders run a short sequence at a time; the graph plus activations is the whole story.
            var encoderTotal = (long)(weightBytes * 1.2);
            return ValueTask.FromResult(new MemoryEstimate(
                OnGpu(options) ? 0 : encoderTotal,
                OnGpu(options) ? encoderTotal : 0,
                FitVerdict.Unknown,
                $"About {OnnxModelFolderReader.Megabytes(encoderTotal)} MB for the encoder graph and its activations."));
        }

        var context = options.ContextSize ?? model.ContextLength ?? folder?.ContextLength ?? netCoreAIOptions.CurrentValue.Models.DefaultContextSize;
        var kv = EstimateKvCacheBytes(folder, context);

        // ONNX Runtime keeps the graph, the weights and its arenas resident on top of the KV cache.
        var total = (long)((weightBytes + kv) * 1.1);
        var explanation = folder?.LayerCount is { } layers
            ? $"About {OnnxModelFolderReader.Megabytes(weightBytes)} MB of weights plus {OnnxModelFolderReader.Megabytes(kv)} MB of KV cache for {context} tokens across {layers} layers."
            : $"About {OnnxModelFolderReader.Megabytes(total)} MB for a {context}-token context (the folder config did not record the layer shape, so the KV cache is estimated).";

        // ONNX Runtime does not split a model across devices: the chosen execution provider takes all of it.
        return ValueTask.FromResult(OnGpu(options)
            ? new MemoryEstimate(0, total, FitVerdict.Unknown, explanation)
            : new MemoryEstimate(total, 0, FitVerdict.Unknown, explanation));
    }

    /// <summary>KV cache size: 2 (K and V) x layers x kv-heads x head-size x context x 2 bytes (fp16).</summary>
    internal static long EstimateKvCacheBytes(OnnxModelFolder? folder, int contextSize)
    {
        const int bytesPerElement = 2;
        if (folder?.LayerCount is not { } layers || folder.HeadSize is not { } headSize || folder.HeadCountKv is not { } kvHeads)
        {
            // No architecture detail: fall back to a per-token figure typical of a small instruct model.
            return contextSize * 512L;
        }

        return 2L * layers * kvHeads * headSize * contextSize * bytesPerElement;
    }

    /// <summary>True when the effective execution provider puts the model in VRAM.</summary>
    private bool OnGpu(LoadOptions options)
    {
        var preference = options.ExecutionProvider == ExecutionProvider.Auto
            ? netCoreAIOptions.CurrentValue.Models.ExecutionProvider
            : options.ExecutionProvider;

        return OnnxExecutionProviders.GenAiProviderName(preference) is not null;
    }

    public ValueTask<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolvePath(model) ?? throw new NetCoreAIException($"Model '{model.Name}' has no folder path; re-import it or download it again.");
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            throw new NetCoreAIException($"ONNX folder for '{model.Name}' is missing at {path}. Re-download the model or fix its path in the registry.");
        }

        var folder = OnnxModelFolderReader.TryRead(path)
            ?? throw new NetCoreAIException(
                $"'{model.Name}' at {path} is not an ONNX model folder: it needs a genai_config.json (generative models) or an .onnx graph with its tokenizer (embedding models).");

        var settings = netCoreAIOptions.CurrentValue.Models;
        var preference = options.ExecutionProvider == ExecutionProvider.Auto ? settings.ExecutionProvider : options.ExecutionProvider;

        return ValueTask.FromResult(folder.Kind == OnnxModelKind.Embedding
            ? LoadEmbedding(model, folder, options, preference, settings)
            : LoadGenerative(model, folder, options, preference, settings));
    }

    private LoadedModel LoadGenerative(ModelDescriptor model, OnnxModelFolder folder, LoadOptions options, ExecutionProvider preference, ModelsOptions settings)
    {
        var contextSize = options.ContextSize ?? Math.Min(model.ContextLength ?? int.MaxValue, settings.DefaultContextSize);
        if (folder.ContextLength is { } trained && contextSize > trained)
        {
            _logger.LogInformation("Requested context {Requested} exceeds the {ModelId} training length {Trained}; using {Trained}.", contextSize, model.Id, trained, trained);
            contextSize = trained;
        }

        contextSize = Math.Max(256, contextSize);
        _logger.LogInformation(
            "Loading ONNX model {ModelId} from {Path} (context {Context}, execution provider {Provider})",
            model.Id, folder.Directory, contextSize, preference);

        Config? config = null;
        Model? onnxModel = null;
        GenAiTokenizer? tokenizer = null;
        try
        {
            config = new Config(folder.Directory);
            OnnxExecutionProviders.TryApply(config, preference, _logger);
            onnxModel = new Model(config);
            tokenizer = new GenAiTokenizer(onnxModel);

            var handle = new OnnxGenerativeModel(model, folder, config, onnxModel, tokenizer, contextSize);
            var used = (long)(folder.GraphBytes * 1.1) + EstimateKvCacheBytes(folder, contextSize);
            return new LoadedModel(model, Id, handle, used, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NetCoreAIException)
        {
            tokenizer?.Dispose();
            onnxModel?.Dispose();
            config?.Dispose();
            throw new NetCoreAIException($"ONNX Runtime GenAI could not load '{model.Name}': {ex.Message}", ex);
        }
    }

    private LoadedModel LoadEmbedding(ModelDescriptor model, OnnxModelFolder folder, LoadOptions options, ExecutionProvider preference, ModelsOptions settings)
    {
        var graph = folder.GraphPath
            ?? throw new NetCoreAIException($"'{model.Name}' has no .onnx graph in {folder.Directory}.");

        _logger.LogInformation("Loading ONNX embedding model {ModelId} from {Path} (execution provider {Provider})", model.Id, graph, preference);

        SessionOptions? sessionOptions = null;
        InferenceSession? session = null;
        try
        {
            sessionOptions = OnnxExecutionProviders.CreateSessionOptions(preference, options.Threads ?? settings.Threads, _logger);
            session = new InferenceSession(graph, sessionOptions);
            var tokenizer = OnnxTokenizerLoader.Load(folder.Directory);

            var handle = new OnnxEmbeddingModel(model, folder, session, tokenizer)
            {
                Dimensions = folder.HiddenSize,
                MaxTokens = Math.Max(16, options.ContextSize ?? folder.ContextLength ?? 512),
                MeanPooling = folder.MeanPooling,
                Normalize = folder.NormalizeEmbeddings,
            };

            return new LoadedModel(model, Id, handle, (long)(folder.GraphBytes * 1.2), options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NetCoreAIException)
        {
            session?.Dispose();
            throw new NetCoreAIException($"ONNX Runtime could not load the embedding model '{model.Name}': {ex.Message}", ex);
        }
        finally
        {
            sessionOptions?.Dispose();
        }
    }

    public ValueTask UnloadAsync(LoadedModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        (model.Handle as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    public IChatClient CreateChatClient(LoadedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Handle is OnnxEmbeddingModel)
        {
            throw new NotSupportedException($"'{model.Descriptor.Name}' is an embedding model and cannot generate chat responses.");
        }

        return new OnnxChatClient(Generative(model), loggerFactory.CreateLogger<OnnxChatClient>());
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Handle is not OnnxEmbeddingModel embedding)
        {
            throw new NotSupportedException(
                $"'{model.Descriptor.Name}' was loaded as a generative model. ONNX Runtime GenAI does not produce embeddings; register a sentence-transformers ONNX export instead.");
        }

        return new OnnxEmbeddingGenerator(embedding, loggerFactory.CreateLogger<OnnxEmbeddingGenerator>());
    }

    private static OnnxGenerativeModel Generative(LoadedModel model) =>
        model.Handle as OnnxGenerativeModel ?? throw new InvalidOperationException($"Model '{model.Descriptor.Id}' was not loaded by the ONNX provider.");

    /// <summary>Reads and caches the folder configuration. Returns null when the folder is absent or unreadable.</summary>
    internal OnnxModelFolder? TryReadFolder(ModelDescriptor model)
    {
        var path = ResolvePath(model);
        if (path is null || (!Directory.Exists(path) && !File.Exists(path)))
        {
            return null;
        }

        return _folderCache.GetOrAdd(path, p =>
        {
            var folder = OnnxModelFolderReader.TryRead(p);
            if (folder is null)
            {
                _logger.LogWarning("Could not read an ONNX model configuration from {Path}.", p);
            }

            return folder!;
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
}
