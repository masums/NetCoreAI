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
        var row = await db.KnowledgeBases.FindAsync([db.CurrentTenant, knowledgeBase.Id], cancellationToken).ConfigureAwait(false);
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
        var row = await db.DataSources.FindAsync([db.CurrentTenant, source.Id], cancellationToken).ConfigureAwait(false);
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
        var row = await db.Documents.FindAsync([db.CurrentTenant, document.Id], cancellationToken).ConfigureAwait(false);
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
        var row = await db.Jobs.FindAsync([db.CurrentTenant, job.Id], cancellationToken).ConfigureAwait(false);
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
        var row = await db.Tools.FindAsync([db.CurrentTenant, tool.Id], cancellationToken).ConfigureAwait(false);
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
        var row = await db.Agents.FindAsync([db.CurrentTenant, agent.Id], cancellationToken).ConfigureAwait(false);
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
        var row = await db.Runs.FindAsync([db.CurrentTenant, run.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new RunRow { Id = run.Id, AgentId = run.AgentId, StartedAtTicks = run.StartedAt.UtcTicks };
            db.Runs.Add(row);
        }

        row.Json = SqliteMetadataStore.Serialize(run);

        // Copied out of the JSON so usage can be filtered and totalled in SQL. Nulls stay null: a run
        // whose provider reported no usage is not a run that used nothing.
        row.ModelId = run.ModelId;
        row.UserId = run.UserId;
        row.InputTokens = run.InputTokens;
        row.OutputTokens = run.OutputTokens;
        row.Cost = run.EstimatedCost is { } cost ? (double)cost : null;
        row.Success = run.Success;
        row.ElapsedMs = run.ElapsedMs;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunTrace>> QueryAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await Filter(db, query)
            .OrderByDescending(r => r.StartedAtTicks)
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit, 1, 500))
            .Select(r => r.Json)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. Search(rows.Select(SqliteMetadataStore.Deserialize<RunTrace>), query.Search)];
    }

    public async Task<int> CountAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await Filter(db, query).CountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageSummary> SummariseAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Only the columns, never the JSON. A month of runs is a lot of text to pull across a process
        // boundary in order to add up seven numbers.
        var rows = await Filter(db, query)
            .Select(r => new Row(r.AgentId, r.ModelId, r.UserId, r.InputTokens, r.OutputTokens, r.Cost, r.Success, r.ElapsedMs, r.StartedAtTicks))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var elapsed = rows.Where(r => r.ElapsedMs is not null).Select(r => r.ElapsedMs!.Value).Order().ToList();

        return new UsageSummary(rows.Count, rows.Count(r => r.Success == false))
        {
            InputTokens = rows.Sum(r => (long?)r.InputTokens ?? 0),
            OutputTokens = rows.Sum(r => (long?)r.OutputTokens ?? 0),
            Cost = rows.Sum(r => (decimal?)r.Cost ?? 0),
            MedianElapsedMs = elapsed.Count == 0 ? 0 : elapsed[elapsed.Count / 2],
            Unmeasured = rows.Count(r => r.InputTokens is null && r.OutputTokens is null),
            ByAgent = Group(rows, r => r.AgentId),
            ByModel = Group(rows, r => r.ModelId ?? "(not recorded)"),
            ByUser = Group(rows, r => r.UserId ?? "(not signed in)"),
            ByDay = Days(rows, query),
        };
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = olderThan.UtcTicks;
        return await db.Runs.Where(r => r.StartedAtTicks < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct Row(
        string AgentId, string? ModelId, string? UserId, int? InputTokens, int? OutputTokens,
        double? Cost, bool? Success, long? ElapsedMs, long StartedAtTicks);

    private static IQueryable<RunRow> Filter(NetCoreAIDbContext db, RunQuery query)
    {
        var rows = db.Runs.AsNoTracking().AsQueryable();

        if (query.AgentId is { Length: > 0 } agent)
        {
            rows = rows.Where(r => r.AgentId == agent);
        }

        if (query.ModelId is { Length: > 0 } model)
        {
            rows = rows.Where(r => r.ModelId == model);
        }

        if (query.UserId is { Length: > 0 } user)
        {
            rows = rows.Where(r => r.UserId == user);
        }

        if (query.Success is { } success)
        {
            rows = rows.Where(r => r.Success == success);
        }

        if (query.Since is { } since)
        {
            var ticks = since.UtcTicks;
            rows = rows.Where(r => r.StartedAtTicks >= ticks);
        }

        if (query.Until is { } until)
        {
            var ticks = until.UtcTicks;
            rows = rows.Where(r => r.StartedAtTicks <= ticks);
        }

        return rows;
    }

    /// <summary>
    /// Narrows a page of runs by free text.
    /// </summary>
    /// <remarks>
    /// Applied after the page is read, not in SQL. The question and the answer live inside the JSON, so a
    /// database-side match would be a scan of every row of text — and the honest version of that is a
    /// full-text index, which is a bigger thing than this. Said plainly so nobody mistakes what this does:
    /// it searches the page you are looking at, not the whole history.
    /// </remarks>
    private static IEnumerable<RunTrace> Search(IEnumerable<RunTrace> runs, string? search) =>
        search is not { Length: > 0 }
            ? runs
            : runs.Where(r =>
                (r.Input?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Output?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

    private static List<UsageBreakdown> Group(List<Row> rows, Func<Row, string> key) =>
        [.. rows.GroupBy(key)
            .Select(g => new UsageBreakdown(g.Key, g.Count())
            {
                Tokens = g.Sum(r => (long?)r.InputTokens ?? 0) + g.Sum(r => (long?)r.OutputTokens ?? 0),
                Cost = g.Sum(r => (decimal?)r.Cost ?? 0),
            })
            .OrderByDescending(b => b.Tokens)
            .ThenBy(b => b.Key, StringComparer.Ordinal)];

    /// <summary>
    /// One entry per day in the period, including the days nothing happened.
    /// </summary>
    /// <remarks>
    /// A chart drawn only from days that have runs joins Monday to Thursday with a straight line, and
    /// invents two days of activity that did not happen.
    /// </remarks>
    private static List<UsageBreakdown> Days(List<Row> rows, RunQuery query)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var first = (query.Since ?? new DateTimeOffset(rows.Min(r => r.StartedAtTicks), TimeSpan.Zero)).UtcDateTime.Date;
        var last = (query.Until ?? new DateTimeOffset(rows.Max(r => r.StartedAtTicks), TimeSpan.Zero)).UtcDateTime.Date;
        var byDay = rows
            .GroupBy(r => new DateTimeOffset(r.StartedAtTicks, TimeSpan.Zero).UtcDateTime.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var days = new List<UsageBreakdown>();
        for (var day = first; day <= last && days.Count < 400; day = day.AddDays(1))
        {
            var hits = byDay.GetValueOrDefault(day) ?? [];
            days.Add(new UsageBreakdown(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), hits.Count)
            {
                Tokens = hits.Sum(r => (long?)r.InputTokens ?? 0) + hits.Sum(r => (long?)r.OutputTokens ?? 0),
                Cost = hits.Sum(r => (decimal?)r.Cost ?? 0),
            });
        }

        return days;
    }
}

/// <summary>API keys. Only hashes are kept, so this table cannot hand anyone a working key.</summary>
internal sealed class SqliteApiKeyStore(IDbContextFactory<NetCoreAIDbContext> factory) : IApiKeyStore
{
    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ApiKeys.AsNoTracking().Select(k => k.Json).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(SqliteMetadataStore.Deserialize<ApiKey>).OrderBy(k => k.Name, StringComparer.Ordinal)];
    }

    public async Task<ApiKey?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.ApiKeys.AsNoTracking().Where(k => k.Id == id).Select(k => k.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<ApiKey>(json);
    }

    public async Task<ApiKey?> FindByHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.ApiKeys.AsNoTracking().Where(k => k.Hash == hash).Select(k => k.Json).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : SqliteMetadataStore.Deserialize<ApiKey>(json);
    }

    public async Task UpsertAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ApiKeys.FindAsync([db.CurrentTenant, key.Id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new ApiKeyRow { Id = key.Id };
            db.ApiKeys.Add(row);
        }

        row.Hash = key.Hash;
        row.Json = SqliteMetadataStore.Serialize(key);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.ApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Who did what. Append-only by construction: there is no update path, and the only delete is retention.
/// </summary>
internal sealed class SqliteAuditStore(IDbContextFactory<NetCoreAIDbContext> factory) : IAuditStore
{
    public async Task<IReadOnlyList<NetCoreAI.Security.AuditEntry>> ListAsync(
        NetCoreAI.Security.AuditFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Audit.AsNoTracking().AsQueryable();

        if (filter.EntityType is { Length: > 0 } type)
        {
            query = query.Where(a => a.EntityType == type);
        }

        if (filter.EntityId is { Length: > 0 } entityId)
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        if (filter.ActorId is { Length: > 0 } actorId)
        {
            query = query.Where(a => a.ActorId == actorId);
        }

        if (filter.Action is { Length: > 0 } action)
        {
            query = query.Where(a => a.Action == action);
        }

        if (filter.Since is { } since)
        {
            var ticks = since.UtcTicks;
            query = query.Where(a => a.AtTicks >= ticks);
        }

        var rows = await query
            .OrderByDescending(a => a.AtTicks)
            .Take(Math.Clamp(filter.Limit, 1, 1000))
            .Select(a => a.Json)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(SqliteMetadataStore.Deserialize<NetCoreAI.Security.AuditEntry>)];
    }

    public async Task WriteAsync(NetCoreAI.Security.AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Audit.Add(new AuditRow
        {
            Id = entry.Id,
            AtTicks = entry.At.UtcTicks,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            ActorId = entry.ActorId,
            Json = SqliteMetadataStore.Serialize(entry),
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = olderThan.UtcTicks;
        return await db.Audit.Where(a => a.AtTicks < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
