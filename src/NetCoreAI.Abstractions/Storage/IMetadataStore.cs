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

    IApiKeyStore ApiKeys { get; }

    IAuditStore Audit { get; }

    IAgentVersionStore AgentVersions { get; }

    IToolGroupStore ToolGroups { get; }

    IEvaluationStore Evaluations { get; }
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
/// <summary>API keys, stored as hashes.</summary>
public interface IApiKeyStore
{
    Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default);
    Task<ApiKey?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Finds a key by the hash of the presented secret. The only way a key is looked up on a call.</summary>
    Task<ApiKey?> FindByHashAsync(string hash, CancellationToken cancellationToken = default);
    Task UpsertAsync(ApiKey key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

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
/// <summary>Which runs to return, and where in the list to start.</summary>
public sealed record RunQuery
{
    public string? AgentId { get; init; }

    public string? ModelId { get; init; }

    public string? UserId { get; init; }

    /// <summary>Only failures, only successes, or both.</summary>
    public bool? Success { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    /// <summary>Free text matched against the question and the answer. Null skips the scan.</summary>
    public string? Search { get; init; }

    public int Limit { get; init; } = 50;

    public int Offset { get; init; }
}

/// <summary>What a set of runs used, in total and broken down.</summary>
/// <param name="Runs">How many runs the totals cover.</param>
/// <param name="Failed">How many of those did not finish.</param>
public sealed record UsageSummary(int Runs, int Failed)
{
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public decimal Cost { get; init; }

    /// <summary>Median rather than mean: one thirty-second run should not move the number people quote.</summary>
    public long MedianElapsedMs { get; init; }

    /// <summary>
    /// Runs whose token counts were never recorded.
    /// </summary>
    /// <remarks>
    /// Reported rather than folded into the totals as zero. A provider that returns no usage, and a run
    /// from before these figures were kept, both land here — and a report that hides them reads as
    /// precise when it is not.
    /// </remarks>
    public int Unmeasured { get; init; }

    public IReadOnlyList<UsageBreakdown> ByAgent { get; init; } = [];

    public IReadOnlyList<UsageBreakdown> ByModel { get; init; } = [];

    public IReadOnlyList<UsageBreakdown> ByUser { get; init; } = [];

    /// <summary>One entry per day in the period, oldest first, so a chart has no gaps to invent.</summary>
    public IReadOnlyList<UsageBreakdown> ByDay { get; init; } = [];
}

/// <summary>One line of a usage breakdown.</summary>
/// <param name="Key">The agent, model, person or day.</param>
/// <param name="Runs">Runs attributed to it.</param>
public sealed record UsageBreakdown(string Key, int Runs)
{
    public long Tokens { get; init; }

    public decimal Cost { get; init; }
}

public interface IRunStore
{
    Task<IReadOnlyList<RunTrace>> ListAsync(string? agentId = null, int limit = 50, CancellationToken cancellationToken = default);
    Task<RunTrace?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(RunTrace run, CancellationToken cancellationToken = default);

    /// <summary>Runs matching a filter, newest first.</summary>
    Task<IReadOnlyList<RunTrace>> QueryAsync(RunQuery query, CancellationToken cancellationToken = default);

    /// <summary>How many runs match, for paging through them.</summary>
    Task<int> CountAsync(RunQuery query, CancellationToken cancellationToken = default);

    /// <summary>What the matching runs used, in total and broken down.</summary>
    Task<UsageSummary> SummariseAsync(RunQuery query, CancellationToken cancellationToken = default);

    /// <summary>Removes runs older than the cutoff, so the table does not grow without bound.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

/// <summary>
/// A store that can copy itself somewhere safe while it is running.
/// </summary>
/// <remarks>
/// Optional: a store implements it if it can. Copying a live database file is the classic way to produce a
/// backup that restores into a corrupt database, because the copy catches it mid-write — so a store that
/// cannot do this properly should not pretend, and the backup service says plainly that no snapshot is
/// available rather than copying the file and hoping.
/// </remarks>
public interface ISnapshotSource
{
    /// <summary>Writes a consistent copy to <paramref name="path"/>, which must not already exist.</summary>
    Task SnapshotAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Evaluation sets and the runs made against them.</summary>
public interface IEvaluationStore
{
    Task<IReadOnlyList<NetCoreAI.Knowledge.EvaluationSet>> ListSetsAsync(string? knowledgeBaseId = null, CancellationToken cancellationToken = default);

    Task<NetCoreAI.Knowledge.EvaluationSet?> GetSetAsync(string id, CancellationToken cancellationToken = default);

    Task UpsertSetAsync(NetCoreAI.Knowledge.EvaluationSet set, CancellationToken cancellationToken = default);

    Task DeleteSetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Runs against a set, newest first. Kept so two configurations can be compared.</summary>
    Task<IReadOnlyList<NetCoreAI.Knowledge.EvaluationRun>> ListRunsAsync(string setId, int limit = 50, CancellationToken cancellationToken = default);

    Task AddRunAsync(NetCoreAI.Knowledge.EvaluationRun run, CancellationToken cancellationToken = default);
}

/// <summary>Named sets of tools.</summary>
public interface IToolGroupStore
{
    Task<IReadOnlyList<ToolGroup>> ListAsync(CancellationToken cancellationToken = default);

    Task<ToolGroup?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task UpsertAsync(ToolGroup group, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Published agent versions. Append-only, and deleted only with the agent they belong to.
/// </summary>
public interface IAgentVersionStore
{
    /// <summary>Every published version of an agent, newest first.</summary>
    Task<IReadOnlyList<AgentVersion>> ListAsync(string agentId, CancellationToken cancellationToken = default);

    Task<AgentVersion?> GetAsync(string agentId, int version, CancellationToken cancellationToken = default);

    Task AddAsync(AgentVersion version, CancellationToken cancellationToken = default);

    /// <summary>Removes an agent's history, when the agent itself goes.</summary>
    Task DeleteAllAsync(string agentId, CancellationToken cancellationToken = default);
}

/// <summary>Who did what. Append-only: there is no update, and the only delete is retention.</summary>
public interface IAuditStore
{
    Task<IReadOnlyList<NetCoreAI.Security.AuditEntry>> ListAsync(NetCoreAI.Security.AuditFilter filter, CancellationToken cancellationToken = default);

    Task WriteAsync(NetCoreAI.Security.AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Removes entries older than the cutoff. The only way a row leaves this table.</summary>
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
