using Microsoft.EntityFrameworkCore;

namespace NetCoreAI.Storage.Sqlite;

/// <summary>
/// Knowledge bases, their data sources and their documents. Follows the same shape as the other stores:
/// a JSON payload plus the few columns that are filtered on.
/// </summary>
internal sealed class SqliteKnowledgeStore(IDbContextFactory<NetCoreAIDbContext> factory) : IKnowledgeStore
{
    public async Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.KnowledgeBases.AsNoTracking().OrderBy(k => k.Name).Select(k => k.Json).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(SqliteMetadataStore.Deserialize<KnowledgeBase>)];
    }

    public async Task<KnowledgeBase?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.KnowledgeBases.AsNoTracking().Where(k => k.Id == id).Select(k => k.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<KnowledgeBase>(json);
    }

    public async Task UpsertAsync(KnowledgeBase knowledgeBase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.KnowledgeBases.FindAsync([knowledgeBase.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new KnowledgeBaseRow { Id = knowledgeBase.Id, CreatedAtTicks = knowledgeBase.CreatedAt.UtcTicks };
            db.KnowledgeBases.Add(row);
        }

        row.Name = knowledgeBase.Name;
        row.EmbeddingModel = knowledgeBase.EmbeddingModel;
        row.Json = SqliteMetadataStore.Serialize(knowledgeBase);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Sources and documents belong to the base; leaving them would orphan rows nothing can reach.
        await db.Documents.Where(d => d.KnowledgeBaseId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.DataSources.Where(s => s.KnowledgeBaseId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.KnowledgeBases.Where(k => k.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DataSourceDefinition>> ListSourcesAsync(string knowledgeBaseId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.DataSources.AsNoTracking()
            .Where(s => s.KnowledgeBaseId == knowledgeBaseId)
            .Select(s => s.Json)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(SqliteMetadataStore.Deserialize<DataSourceDefinition>).OrderBy(s => s.CreatedAt)];
    }

    public async Task<DataSourceDefinition?> GetSourceAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.DataSources.AsNoTracking().Where(s => s.Id == id).Select(s => s.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<DataSourceDefinition>(json);
    }

    public async Task UpsertSourceAsync(DataSourceDefinition source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.DataSources.FindAsync([source.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new DataSourceRow { Id = source.Id, KnowledgeBaseId = source.KnowledgeBaseId };
            db.DataSources.Add(row);
        }

        row.Type = source.Type;
        row.Enabled = source.Enabled;
        row.Json = SqliteMetadataStore.Serialize(source);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSourceAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DataSources.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string knowledgeBaseId, string? dataSourceId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Documents.AsNoTracking().Where(d => d.KnowledgeBaseId == knowledgeBaseId);
        if (dataSourceId is not null)
        {
            query = query.Where(d => d.DataSourceId == dataSourceId);
        }

        var rows = await query.OrderByDescending(d => d.IngestedAtTicks).Select(d => d.Json).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(SqliteMetadataStore.Deserialize<KnowledgeDocument>)];
    }

    public async Task<KnowledgeDocument?> GetDocumentAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => d.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<KnowledgeDocument>(json);
    }

    public async Task<KnowledgeDocument?> FindByHashAsync(string knowledgeBaseId, string contentHash, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Indexed on (KnowledgeBaseId, ContentHash): re-ingest asks this once per document.
        var json = await db.Documents.AsNoTracking()
            .Where(d => d.KnowledgeBaseId == knowledgeBaseId && d.ContentHash == contentHash)
            .Select(d => d.Json)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return json is null ? null : SqliteMetadataStore.Deserialize<KnowledgeDocument>(json);
    }

    public async Task UpsertDocumentAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Documents.FindAsync([document.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new DocumentRow { Id = document.Id, KnowledgeBaseId = document.KnowledgeBaseId };
            db.Documents.Add(row);
        }

        row.DataSourceId = document.DataSourceId;
        row.Title = document.Title;
        row.ContentHash = document.ContentHash;
        row.IngestedAtTicks = document.IngestedAt.UtcTicks;
        row.Json = SqliteMetadataStore.Serialize(document);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDocumentAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Background jobs, persisted so progress and failures survive a restart.</summary>
internal sealed class SqliteJobStore(IDbContextFactory<NetCoreAIDbContext> factory) : IJobStore
{
    public async Task<IReadOnlyList<JobRecord>> ListAsync(string? targetId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Jobs.AsNoTracking().AsQueryable();
        if (targetId is not null)
        {
            query = query.Where(j => j.TargetId == targetId);
        }

        var rows = await query.OrderByDescending(j => j.CreatedAtTicks).Select(j => j.Json).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(SqliteMetadataStore.Deserialize<JobRecord>)];
    }

    public async Task<JobRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.Jobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<JobRecord>(json);
    }

    public async Task UpsertAsync(JobRecord job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Jobs.FindAsync([job.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new JobRow { Id = job.Id, Type = job.Type, TargetId = job.TargetId, CreatedAtTicks = job.CreatedAt.UtcTicks };
            db.Jobs.Add(row);
        }

        row.State = job.State.ToString();
        row.Json = SqliteMetadataStore.Serialize(job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Jobs.Where(j => j.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = olderThan.UtcTicks;

        // Only terminal jobs: a long-running ingest must survive housekeeping.
        var terminal = new[] { nameof(JobState.Completed), nameof(JobState.Failed), nameof(JobState.Cancelled) };
        return await db.Jobs
            .Where(j => j.CreatedAtTicks < cutoff && terminal.Contains(j.State))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Saved tool definitions.
/// </summary>
/// <remarks>
/// The name is a column rather than only a JSON field because it is looked up on every tool call and has
/// to be unique: two tools sharing a name would make a model's call ambiguous, and the store is the only
/// place that can refuse it.
/// </remarks>
internal sealed class SqliteToolStore(IDbContextFactory<NetCoreAIDbContext> factory) : IToolStore
{
    public async Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Tools.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Json).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(SqliteMetadataStore.Deserialize<ToolDefinition>)];
    }

    public async Task<ToolDefinition?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.Tools.AsNoTracking().Where(t => t.Id == id).Select(t => t.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<ToolDefinition>(json);
    }

    public async Task<ToolDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.Tools.AsNoTracking().Where(t => t.Name == name).Select(t => t.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<ToolDefinition>(json);
    }

    public async Task UpsertAsync(ToolDefinition tool, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Tools.FindAsync([tool.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new ToolRow { Id = tool.Id };
            db.Tools.Add(row);
        }

        row.Name = tool.Name;
        row.Kind = tool.Kind.ToString();
        row.Json = SqliteMetadataStore.Serialize(tool);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Tools.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Agent definitions.</summary>
internal sealed class SqliteAgentStore(IDbContextFactory<NetCoreAIDbContext> factory) : IAgentStore
{
    public async Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Agents.AsNoTracking().OrderBy(a => a.Name).Select(a => a.Json).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(SqliteMetadataStore.Deserialize<AgentDefinition>)];
    }

    public async Task<AgentDefinition?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.Agents.AsNoTracking().Where(a => a.Id == id).Select(a => a.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<AgentDefinition>(json);
    }

    public async Task UpsertAsync(AgentDefinition agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Agents.FindAsync([agent.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new AgentRow { Id = agent.Id };
            db.Agents.Add(row);
        }

        row.Name = agent.Name;
        row.Json = SqliteMetadataStore.Serialize(agent);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Agents.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Run traces.
/// </summary>
/// <remarks>
/// The fastest-growing table here: one row per agent run, each carrying every tool call. Listing is capped
/// and pruning is by age, because "show me the runs" on a busy host must not read a year of them.
/// </remarks>
internal sealed class SqliteRunStore(IDbContextFactory<NetCoreAIDbContext> factory) : IRunStore
{
    public async Task<IReadOnlyList<RunTrace>> ListAsync(string? agentId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Runs.AsNoTracking().AsQueryable();
        if (agentId is not null)
        {
            query = query.Where(r => r.AgentId == agentId);
        }

        var rows = await query
            .OrderByDescending(r => r.StartedAtTicks)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(r => r.Json)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(SqliteMetadataStore.Deserialize<RunTrace>)];
    }

    public async Task<RunTrace?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.Runs.AsNoTracking().Where(r => r.Id == id).Select(r => r.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<RunTrace>(json);
    }

    public async Task UpsertAsync(RunTrace run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Runs.FindAsync([run.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new RunRow { Id = run.Id, AgentId = run.AgentId, StartedAtTicks = run.StartedAt.UtcTicks };
            db.Runs.Add(row);
        }

        row.Json = SqliteMetadataStore.Serialize(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = olderThan.UtcTicks;
        return await db.Runs.Where(r => r.StartedAtTicks < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
