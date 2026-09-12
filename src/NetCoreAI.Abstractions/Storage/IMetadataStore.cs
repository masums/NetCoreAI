namespace NetCoreAI;

/// <summary>
/// Persistence for everything except model files and vectors. One implementation per package
/// (SQLite default, SQL Server / PostgreSQL later). All members must be safe for concurrent use.
/// </summary>
public interface IMetadataStore
{
    /// <summary>Creates or migrates the schema. Called once by a hosted service at startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Cheap connectivity check used by the health endpoint.</summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);

    IModelStore Models { get; }

    IAliasStore Aliases { get; }

    IProviderConnectionStore Connections { get; }

    IChatSessionStore Sessions { get; }

    ISettingsStore Settings { get; }

    IDownloadStore Downloads { get; }
}

public interface IModelStore
{
    Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken cancellationToken = default);
    Task<ModelDescriptor?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(ModelDescriptor model, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public interface IAliasStore
{
    Task<IReadOnlyList<ModelAlias>> ListAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(ModelAlias alias, CancellationToken cancellationToken = default);
    Task DeleteAsync(string alias, CancellationToken cancellationToken = default);
}

public interface IProviderConnectionStore
{
    Task<IReadOnlyList<ProviderConnection>> ListAsync(CancellationToken cancellationToken = default);
    Task<ProviderConnection?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(ProviderConnection connection, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public interface IChatSessionStore
{
    Task<IReadOnlyList<ChatSession>> ListAsync(string? userId, CancellationToken cancellationToken = default);
    Task<ChatSession?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(ChatSession session, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessageRecord>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default);
    Task AppendMessageAsync(ChatMessageRecord message, CancellationToken cancellationToken = default);
    Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<ChatMessageRecord> messages, CancellationToken cancellationToken = default);
}

/// <summary>Key/value settings persisted from the UI; override appsettings values.</summary>
public interface ISettingsStore
{
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

public interface IDownloadStore
{
    Task<IReadOnlyList<DownloadJob>> ListAsync(CancellationToken cancellationToken = default);
    Task<DownloadJob?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
