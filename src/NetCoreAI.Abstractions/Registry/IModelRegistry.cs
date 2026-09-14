using Microsoft.Extensions.AI;

namespace NetCoreAI;

/// <summary>Live view of a model in the registry: descriptor plus runtime state.</summary>
public sealed record ModelEntry(ModelDescriptor Descriptor, ModelStatus Status, string? StatusMessage = null, LoadedModel? Loaded = null);

/// <summary>
/// The catalogue of local and remote models together with their runtime state. Backed by <see cref="IMetadataStore"/>;
/// load/unload is delegated to the lifecycle manager.
/// </summary>
public interface IModelRegistry
{
    Task<IReadOnlyList<ModelEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<ModelEntry?> GetAsync(string idOrAlias, CancellationToken cancellationToken = default);

    Task<ModelDescriptor> RegisterAsync(ModelDescriptor model, CancellationToken cancellationToken = default);

    Task<ModelDescriptor> UpdateAsync(ModelDescriptor model, CancellationToken cancellationToken = default);

    /// <summary>Unloads if loaded, removes registry row; optionally deletes local files.</summary>
    Task RemoveAsync(string id, bool deleteFiles, CancellationToken cancellationToken = default);

    Task<LoadedModel> LoadAsync(string idOrAlias, LoadOptions? options = null, CancellationToken cancellationToken = default);

    Task UnloadAsync(string idOrAlias, CancellationToken cancellationToken = default);

    /// <summary>Alias management: "fast", "quality", "embed", "default"... Aliases resolve in <see cref="IChatClientFactory"/>.</summary>
    Task SetAliasAsync(string alias, string modelId, IReadOnlyList<string>? fallbackModelIds = null, CancellationToken cancellationToken = default);

    Task RemoveAliasAsync(string alias, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ModelAlias>> GetAliasesAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised on any status or descriptor change so the dashboard can refresh without polling.</summary>
    event EventHandler<ModelEntry>? Changed;
}

/// <summary>An alias pointing at a primary model with an ordered fallback list (mixed local/remote allowed).</summary>
public sealed record ModelAlias(string Alias, string ModelId, IReadOnlyList<string> FallbackModelIds)
{
    /// <summary>Well-known alias used when code asks for "the" chat model.</summary>
    public const string Default = "default";

    /// <summary>Well-known alias used by knowledge bases when no embedding model is configured.</summary>
    public const string Embed = "embed";
}

/// <summary>Resolves models by id or alias into ready-to-use Microsoft.Extensions.AI clients wrapped in the NetCoreAI middleware pipeline.</summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Returns a chat client for the alias or model id. The client lazily loads the model on first call and applies
    /// alias fallbacks. Throws <see cref="ModelNotFoundException"/> when nothing matches.
    /// </summary>
    IChatClient Get(string idOrAlias = ModelAlias.Default);

    IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(string idOrAlias = ModelAlias.Embed);

    bool TryGet(string idOrAlias, out IChatClient? client);
}
