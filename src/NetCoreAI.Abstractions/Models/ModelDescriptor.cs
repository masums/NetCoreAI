namespace NetCoreAI;

/// <summary>Weight/serving format of a model.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ModelFormat>))]
public enum ModelFormat
{
    /// <summary>llama.cpp GGUF file.</summary>
    Gguf,
    /// <summary>ONNX Runtime GenAI folder (genai_config.json) or plain ONNX embedding model.</summary>
    Onnx,
    /// <summary>Hugging Face Safetensors repository (converted on import in v1).</summary>
    Safetensors,
    /// <summary>Model served by a remote provider (Ollama, OpenAI-compatible, Anthropic...). No local files.</summary>
    Remote,
}

/// <summary>Whether a provider executes models in-process or calls a remote service.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ProviderKind>))]
public enum ProviderKind
{
    Local,
    Remote,
}

/// <summary>Lifecycle state of a model in the registry.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ModelStatus>))]
public enum ModelStatus
{
    /// <summary>Registered and present on disk / reachable, not loaded.</summary>
    Available,
    /// <summary>Files are still being downloaded or imported.</summary>
    Downloading,
    Loading,
    Loaded,
    Unloading,
    /// <summary>Last load or health check failed; see <see cref="ModelDescriptor.Tags"/> or logs.</summary>
    Error,
}

/// <summary>Capability flags a model advertises. The UI hides features the model does not support.</summary>
[Flags]
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ModelCapability>))]
public enum ModelCapability
{
    None = 0,
    Chat = 1 << 0,
    Embeddings = 1 << 1,
    ToolCalling = 1 << 2,
    Vision = 1 << 3,
    /// <summary>Provider can force syntactically valid JSON output.</summary>
    JsonMode = 1 << 4,
    /// <summary>Provider can constrain output to a JSON schema (grammar or native).</summary>
    StructuredOutput = 1 << 5,
    Streaming = 1 << 6,
}

/// <summary>Queryable capabilities of a model as reported by its provider.</summary>
/// <param name="Flags">Supported features.</param>
/// <param name="MaxContext">Maximum context length in tokens, when known.</param>
/// <param name="EmbeddingDimensions">Vector size for embedding models, when known.</param>
public sealed record ModelCapabilities(ModelCapability Flags, int? MaxContext = null, int? EmbeddingDimensions = null)
{
    public static readonly ModelCapabilities None = new(ModelCapability.None);

    public bool Supports(ModelCapability capability) => (Flags & capability) == capability;
}

/// <summary>Generation defaults stored per model, per alias or per agent. Null means "provider default".</summary>
public sealed record ModelParameters
{
    public float? Temperature { get; init; }
    public float? TopP { get; init; }
    public int? TopK { get; init; }
    public int? MaxOutputTokens { get; init; }
    public float? RepeatPenalty { get; init; }
    public float? FrequencyPenalty { get; init; }
    public float? PresencePenalty { get; init; }
    public long? Seed { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }

    public static readonly ModelParameters Empty = new();
}

/// <summary>
/// Everything the registry knows about a model, local or remote. Immutable; the registry replaces the record on change.
/// </summary>
public sealed record ModelDescriptor
{
    /// <summary>Stable identifier (URL-safe, unique in the registry).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name shown in the UI.</summary>
    public required string Name { get; init; }

    /// <summary>Weight/serving format.</summary>
    public required ModelFormat Format { get; init; }

    /// <summary>Id of the <see cref="IModelProvider"/> that serves this model.</summary>
    public required string ProviderId { get; init; }

    /// <summary>For remote models: the provider connection this model belongs to.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>For remote models: the model name as the remote service knows it (e.g. "gpt-4o-mini", "llama3.2").</summary>
    public string? RemoteModelId { get; init; }

    /// <summary>Local file or folder path, relative to the data directory when not rooted.</summary>
    public string? Path { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>Quantization label such as Q4_K_M, int4, fp16.</summary>
    public string? Quantization { get; init; }

    /// <summary>Training context length in tokens.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Architecture family (llama, qwen2, phi3, bert...).</summary>
    public string? Family { get; init; }

    /// <summary>Approximate parameter count, used by the fit estimator.</summary>
    public long? ParameterCount { get; init; }

    /// <summary>Chat template override (Jinja or provider-specific). Null = use model metadata.</summary>
    public string? ChatTemplate { get; init; }

    public ModelParameters DefaultParameters { get; init; } = ModelParameters.Empty;

    /// <summary>Where the model came from: "huggingface:repo", "import:path", "url:...", "connection:id".</summary>
    public string? Source { get; init; }

    public string? Revision { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>Capabilities as last reported by the provider.</summary>
    public ModelCapabilities Capabilities { get; init; } = ModelCapabilities.None;

    /// <summary>License identifier or URL surfaced from the hub.</summary>
    public string? License { get; init; }

    /// <summary>Load this model when the host starts.</summary>
    public bool LoadOnStartup { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Notes { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>True when the model is served by a remote provider.</summary>
    public bool IsRemote => Format == ModelFormat.Remote;
}
