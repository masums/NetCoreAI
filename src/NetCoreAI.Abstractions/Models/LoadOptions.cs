namespace NetCoreAI;

/// <summary>Preferred execution hardware for local providers.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ExecutionProvider>))]
public enum ExecutionProvider
{
    Auto,
    Cpu,
    Cuda,
    DirectML,
    Vulkan,
    Metal,
    Npu,
}

/// <summary>KV cache precision for GGUF backends.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<KvCacheType>))]
public enum KvCacheType
{
    Default,
    F16,
    Q8,
    Q4,
}

/// <summary>How concurrent requests to one loaded model are handled.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ConcurrencyPolicy>))]
public enum ConcurrencyPolicy
{
    /// <summary>One generation at a time; others queue (default, safest for VRAM).</summary>
    SingleSlot,
    /// <summary>A pool of N contexts; requests beyond N queue.</summary>
    Pool,
    /// <summary>Fail immediately with a busy error when the slot is taken.</summary>
    RejectWhenBusy,
}

/// <summary>Options applied when a provider loads a model. Null means "provider/auto default".</summary>
public sealed record LoadOptions
{
    public int? ContextSize { get; init; }

    /// <summary>Number of layers offloaded to the GPU; -1 = all, 0 = CPU only.</summary>
    public int? GpuLayers { get; init; }

    public int? BatchSize { get; init; }

    public bool? FlashAttention { get; init; }

    public KvCacheType KvCacheType { get; init; } = KvCacheType.Default;

    public int? Threads { get; init; }

    public ExecutionProvider ExecutionProvider { get; init; } = ExecutionProvider.Auto;

    public ConcurrencyPolicy Concurrency { get; init; } = ConcurrencyPolicy.SingleSlot;

    /// <summary>Pool size when <see cref="Concurrency"/> is <see cref="ConcurrencyPolicy.Pool"/>.</summary>
    public int PoolSize { get; init; } = 1;

    /// <summary>Extra provider-specific settings (e.g. "rope_freq_base").</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }

    public static readonly LoadOptions Default = new();
}

/// <summary>A model that a provider has loaded (or, for remote providers, a ready client). Dispose via <see cref="IModelProvider.UnloadAsync"/>.</summary>
public sealed class LoadedModel
{
    public LoadedModel(ModelDescriptor descriptor, string providerId, object? handle, long memoryBytes, LoadOptions options)
    {
        Descriptor = descriptor;
        ProviderId = providerId;
        Handle = handle;
        MemoryBytes = memoryBytes;
        Options = options;
        LoadedAt = DateTimeOffset.UtcNow;
    }

    public ModelDescriptor Descriptor { get; }

    public string ProviderId { get; }

    /// <summary>Provider-private handle (native weights, client instance). Never exposed to consumers.</summary>
    public object? Handle { get; }

    /// <summary>Approximate RAM + VRAM consumed by this load; 0 for remote models.</summary>
    public long MemoryBytes { get; }

    public LoadOptions Options { get; }

    public DateTimeOffset LoadedAt { get; }

    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Result of the "will it fit" check.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<FitVerdict>))]
public enum FitVerdict
{
    Fits,
    /// <summary>Fits, but with less than 10 % headroom.</summary>
    Tight,
    WontFit,
    Unknown,
}

/// <summary>Memory needed to load a model with given options, split by device.</summary>
public sealed record MemoryEstimate(long RamBytes, long VramBytes, FitVerdict Verdict, string? Explanation = null)
{
    public long TotalBytes => RamBytes + VramBytes;

    public static readonly MemoryEstimate Unknown = new(0, 0, FitVerdict.Unknown, "Provider did not supply an estimate.");
}
