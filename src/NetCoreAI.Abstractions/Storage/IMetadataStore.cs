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

    IKnowledgeStore Knowledge { get; }

    IJobStore Jobs { get; }

    IToolStore Tools { get; }

    IAgentStore Agents { get; }

    IRunStore Runs { get; }
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

/// <summary>Knowledge bases, their data sources and the documents ingested into them.</summary>
public interface IKnowledgeStore
{
    Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken cancellationToken = default);
    Task<KnowledgeBase?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(KnowledgeBase knowledgeBase, CancellationToken cancellationToken = default);

    /// <summary>Removes the base, its sources and its document rows. The vector collection is dropped separately.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DataSourceDefinition>> ListSourcesAsync(string knowledgeBaseId, CancellationToken cancellationToken = default);
    Task<DataSourceDefinition?> GetSourceAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertSourceAsync(DataSourceDefinition source, CancellationToken cancellationToken = default);
    Task DeleteSourceAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string knowledgeBaseId, string? dataSourceId = null, CancellationToken cancellationToken = default);
    Task<KnowledgeDocument?> GetDocumentAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Finds a document by its content hash, which is how re-ingest skips unchanged content.</summary>
    Task<KnowledgeDocument?> FindByHashAsync(string knowledgeBaseId, string contentHash, CancellationToken cancellationToken = default);

    Task UpsertDocumentAsync(KnowledgeDocument document, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for background jobs, so progress and failures survive a restart.</summary>
/// <summary>Agent definitions.</summary>
public interface IAgentStore
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default);
    Task<AgentDefinition?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(AgentDefinition agent, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// What each run of an agent did.
/// </summary>
/// <remarks>
/// Traces are the only way to explain a wrong answer after the fact — which passages it read, which tools
/// it called, with what. They are also the fastest-growing table in the system, so they are pruned.
/// </remarks>
public interface IRunStore
{
    Task<IReadOnlyList<RunTrace>> ListAsync(string? agentId = null, int limit = 50, CancellationToken cancellationToken = default);
    Task<RunTrace?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(RunTrace run, CancellationToken cancellationToken = default);

    /// <summary>Removes runs older than the cutoff, so the table does not grow without bound.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

/// <summary>Saved tool definitions. A tool exists because somebody saved one, never because discovery saw it.</summary>
public interface IToolStore
{
    Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken cancellationToken = default);
    Task<ToolDefinition?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Looks a tool up by the name the model calls, which must be unique across the host.</summary>
    Task<ToolDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task UpsertAsync(ToolDefinition tool, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public interface IJobStore
{
    Task<IReadOnlyList<JobRecord>> ListAsync(string? targetId = null, CancellationToken cancellationToken = default);
    Task<JobRecord?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(JobRecord job, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes terminal jobs older than the cutoff, so the table does not grow without bound.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}
