namespace NetCoreAI;

/// <summary>Health of a remote provider connection as of the last test.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ConnectionHealth>))]
public enum ConnectionHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unreachable,
    AuthFailed,
    Disabled,
}

/// <summary>A named, credentialed configuration of a remote provider (base URL, key, defaults).</summary>
public sealed record ProviderConnection
{
    public required string Id { get; init; }

    /// <summary>Display name such as "OpenAI-prod" or "Office Ollama box".</summary>
    public required string Name { get; init; }

    /// <summary><see cref="IModelProvider.Id"/> of the provider that owns this connection.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Preset id from <see cref="IConnectionAwareProvider.Presets"/> (e.g. "openai", "azure", "groq", "custom").</summary>
    public string? Preset { get; init; }

    public string? BaseUrl { get; init; }

    /// <summary>
    /// Protected (encrypted) secret; never returned to the UI. Use <see cref="ISecretProtector"/> to unprotect.
    /// May be overridden by the environment variable NETCOREAI__CONNECTIONS__{NAME}__SECRET.
    /// </summary>
    public string? ProtectedSecret { get; init; }

    /// <summary>Provider-specific extras (Azure deployment/api-version, organisation id, custom headers...).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ModelParameters DefaultParameters { get; init; } = ModelParameters.Empty;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    public int MaxRetries { get; init; } = 2;

    public int MaxConcurrency { get; init; } = 8;

    /// <summary>Requests per minute allowed through this connection; null = unlimited.</summary>
    public int? RateLimitPerMinute { get; init; }

    /// <summary>Cost per 1K input tokens in the host's currency, for usage reporting.</summary>
    public decimal? CostPer1KInputTokens { get; init; }

    public decimal? CostPer1KOutputTokens { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset? LastHealthCheckAt { get; init; }

    public ConnectionHealth Health { get; init; } = ConnectionHealth.Unknown;

    public string? HealthMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A ready-made connection template for a provider.</summary>
/// <param name="Id">Preset id stored in <see cref="ProviderConnection.Preset"/>.</param>
/// <param name="DisplayName">Shown in the UI.</param>
/// <param name="DefaultBaseUrl">Prefilled base URL; null when the user must supply one.</param>
/// <param name="RequiresApiKey">Whether a secret is mandatory.</param>
/// <param name="SupportsModelListing">Whether the remote API can enumerate models.</param>
/// <param name="CuratedModels">Fallback model ids when listing is unsupported or fails.</param>
/// <param name="SupportsEmbeddings">Whether embeddings are available through this preset.</param>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    string? DefaultBaseUrl,
    bool RequiresApiKey,
    bool SupportsModelListing,
    IReadOnlyList<string> CuratedModels,
    bool SupportsEmbeddings = true)
{
    /// <summary>Extra settings keys the preset needs (e.g. Azure "deployment", "api-version").</summary>
    public IReadOnlyList<string> RequiredSettings { get; init; } = [];
}

/// <summary>Outcome of <see cref="IConnectionAwareProvider.TestConnectionAsync"/>.</summary>
public sealed record ConnectionTestResult(bool Success, ConnectionHealth Health, string Message, IReadOnlyList<string> Models, TimeSpan Latency)
{
    public static ConnectionTestResult Ok(IReadOnlyList<string> models, TimeSpan latency, string? message = null)
        => new(true, ConnectionHealth.Healthy, message ?? $"Reachable, {models.Count} model(s) listed.", models, latency);

    public static ConnectionTestResult Failed(ConnectionHealth health, string message, TimeSpan latency)
        => new(false, health, message, [], latency);
}

/// <summary>Encrypts and decrypts secrets (API keys, tokens, connection strings). Backed by ASP.NET Core Data Protection in hosts.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
