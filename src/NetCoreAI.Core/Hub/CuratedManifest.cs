using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>One entry in the curated "Recommended" list.</summary>
/// <param name="RepoId">Repository on the source hub.</param>
/// <param name="Name">Display name.</param>
/// <param name="Summary">One line on what the model is for.</param>
/// <param name="Files">Files to download for the recommended variant.</param>
public sealed record RecommendedModel(string RepoId, string Name, string Summary, IReadOnlyList<string> Files)
{
    public string SourceId { get; init; } = HuggingFaceClient.SourceId;

    public ModelFormat Format { get; init; } = ModelFormat.Gguf;

    /// <summary>Category shown as a group heading: "chat", "embedding", "code", "vision".</summary>
    public string Category { get; init; } = "chat";

    public string? Quantization { get; init; }

    public long SizeBytes { get; init; }

    public long? ParameterCount { get; init; }

    public int? ContextLength { get; init; }

    public string? License { get; init; }

    /// <summary>Suggested alias, so one click can make a model the default chat or embedding model.</summary>
    public string? Alias { get; init; }

    /// <summary>Whether this variant fits the current machine; filled in by the hub service.</summary>
    public MemoryEstimate? Fit { get; init; }
}

/// <summary>The curated list as published.</summary>
public sealed record RecommendedManifest(IReadOnlyList<RecommendedModel> Models)
{
    public int Version { get; init; } = 1;

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>True when this copy came from the package rather than the network.</summary>
    public bool IsEmbeddedFallback { get; init; }
}

/// <summary>
/// Serves the curated "Recommended" list: fetched from the published manifest, cached in memory for the
/// day, and backed by a copy compiled into this package so a first run with no network still has something
/// to offer.
/// </summary>
public sealed class CuratedManifestService(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<CuratedManifestService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private RecommendedManifest? _cached;
    private DateTimeOffset _fetchedAt;

    public async Task<RecommendedManifest> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && _cached is { } cached && DateTimeOffset.UtcNow - _fetchedAt < CacheLifetime)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _cached is { } current && DateTimeOffset.UtcNow - _fetchedAt < CacheLifetime)
            {
                return current;
            }

            var manifest = await FetchAsync(cancellationToken).ConfigureAwait(false) ?? Embedded;
            _cached = manifest;
            _fetchedAt = DateTimeOffset.UtcNow;
            return manifest;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RecommendedManifest?> FetchAsync(CancellationToken cancellationToken)
    {
        var url = options.CurrentValue.Network.RecommendedManifestUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            using var client = httpClientFactory.CreateClient(NetCoreAIHttp.HubClient);
            var manifest = await client.GetFromJsonAsync<RecommendedManifest>(new Uri(url), Json, cancellationToken).ConfigureAwait(false);
            if (manifest is { Models.Count: > 0 })
            {
                logger.LogDebug("Loaded {Count} recommended models from {Url}.", manifest.Models.Count, url);
                return manifest;
            }

            logger.LogDebug("The recommended manifest at {Url} was empty; using the built-in list.", url);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OfflineModeException or NotSupportedException or UriFormatException)
        {
            // Offline, blocked or unreachable: the built-in list is the point of having one.
            logger.LogDebug(ex, "Could not fetch the recommended manifest from {Url}; using the built-in list.", url);
            return null;
        }
    }

    /// <summary>
    /// The list compiled into the package. Deliberately short and conservative: small, permissively licensed
    /// models that run on a laptop CPU, which is what a first run needs.
    /// </summary>
    internal static RecommendedManifest Embedded { get; } = new(
    [
        new RecommendedModel(
            "Qwen/Qwen2.5-0.5B-Instruct-GGUF",
            "Qwen2.5 0.5B Instruct",
            "Tiny chat model that answers in a second on any CPU. Good for a first run and for tests.",
            ["qwen2.5-0.5b-instruct-q4_k_m.gguf"])
        {
            Quantization = "Q4_K_M",
            SizeBytes = 491_400_032,
            ParameterCount = 494_000_000,
            ContextLength = 32768,
            License = "apache-2.0",
            Alias = "fast",
        },
        new RecommendedModel(
            "bartowski/Llama-3.2-3B-Instruct-GGUF",
            "Llama 3.2 3B Instruct",
            "General-purpose chat with tool calling. Comfortable on 8 GB of RAM.",
            ["Llama-3.2-3B-Instruct-Q4_K_M.gguf"])
        {
            Quantization = "Q4_K_M",
            SizeBytes = 2_019_377_696,
            ParameterCount = 3_210_000_000,
            ContextLength = 131072,
            License = "llama3.2",
            Alias = "default",
        },
        new RecommendedModel(
            "bartowski/Qwen2.5-7B-Instruct-GGUF",
            "Qwen2.5 7B Instruct",
            "Stronger reasoning and tool use when there is 16 GB of RAM or a 8 GB GPU to spare.",
            ["Qwen2.5-7B-Instruct-Q4_K_M.gguf"])
        {
            Quantization = "Q4_K_M",
            SizeBytes = 4_683_073_344,
            ParameterCount = 7_620_000_000,
            ContextLength = 32768,
            License = "apache-2.0",
            Alias = "quality",
        },
        new RecommendedModel(
            "nomic-ai/nomic-embed-text-v1.5-GGUF",
            "Nomic Embed Text v1.5",
            "Embedding model for knowledge bases: 768 dimensions, 8k context.",
            ["nomic-embed-text-v1.5.Q4_K_M.gguf"])
        {
            Category = "embedding",
            Quantization = "Q4_K_M",
            SizeBytes = 84_106_688,
            ContextLength = 8192,
            License = "apache-2.0",
            Alias = "embed",
        },
        new RecommendedModel(
            "sentence-transformers/all-MiniLM-L6-v2",
            "all-MiniLM-L6-v2",
            "Small, fast ONNX embedding model: 384 dimensions, runs on the CPU.",
            ["onnx/model.onnx", "config.json", "vocab.txt", "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json", "modules.json", "1_Pooling/config.json"])
        {
            Format = ModelFormat.Onnx,
            Category = "embedding",
            SizeBytes = 90_918_469,
            ContextLength = 512,
            License = "apache-2.0",
        },
    ])
    {
        IsEmbeddedFallback = true,
    };
}
