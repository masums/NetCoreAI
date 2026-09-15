using System.Collections.Concurrent;

namespace NetCoreAI.Storage;

/// <summary>
/// Non-persistent store used by tests and as the fallback when no storage package is registered
/// (a warning is logged in that case; nothing survives a restart).
/// </summary>
public sealed class InMemoryMetadataStore(NetCoreAI.Tenancy.ITenantAccessor? tenants = null) : IMetadataStore
{
    /// <summary>
    /// One set of dictionaries per tenant.
    /// </summary>
    /// <remarks>
    /// A partition rather than a tenant column, because these stores answer <c>ListAsync</c> from
    /// <c>Values</c> and a column would need every one of them to remember to filter. Isolation that
    /// depends on fourteen classes remembering is isolation that will be wrong once.
    /// </remarks>
    private readonly ConcurrentDictionary<string, Partition> _tenants = new(StringComparer.Ordinal);

    /// <summary>
    /// Settings are host-wide, not a tenant's.
    /// </summary>
    /// <remarks>
    /// The tenant list itself lives in settings, so a per-tenant settings store would make the list of
    /// tenants something each tenant kept privately — which is the one arrangement that cannot work.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string> _settings = new(StringComparer.OrdinalIgnoreCase);

    private Partition Current => _tenants.GetOrAdd(
        tenants?.Current ?? NetCoreAI.Tenancy.TenantId.Default,
        _ => new Partition());

    public IModelStore Models => Current.Models;
    public IAliasStore Aliases => Current.Aliases;
    public IProviderConnectionStore Connections => Current.Connections;
    public IChatSessionStore Sessions => Current.Sessions;
    public ISettingsStore Settings => field ??= new SettingsStore(_settings);
    public IDownloadStore Downloads => Current.Downloads;
    public IKnowledgeStore Knowledge => Current.Knowledge;
    public IJobStore Jobs => Current.Jobs;
    public IToolStore Tools => Current.Tools;
    public IAgentStore Agents => Current.Agents;
    public IRunStore Runs => Current.Runs;
    public IApiKeyStore ApiKeys => Current.ApiKeys;
    public IAuditStore Audit => Current.Audit;

    /// <summary>Everything one tenant owns.</summary>
    private sealed class Partition
    {
        private readonly ConcurrentDictionary<string, ModelDescriptor> _models = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ModelAlias> _aliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ProviderConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, List<ChatMessageRecord>> _messages = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DownloadJob> _downloads = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, KnowledgeBase> _knowledgeBases = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DataSourceDefinition> _dataSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, KnowledgeDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, JobRecord> _jobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AgentDefinition> _agents = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, RunTrace> _runs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ApiKey> _apiKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, NetCoreAI.Security.AuditEntry> _audit = new(StringComparer.OrdinalIgnoreCase);

        public Partition()
        {
            Models = new ModelStore(_models);
            Aliases = new AliasStore(_aliases);
            Connections = new ConnectionStore(_connections);
            Sessions = new SessionStore(_sessions, _messages);
            Downloads = new DownloadStore(_downloads);
            Knowledge = new KnowledgeStore(_knowledgeBases, _dataSources, _documents);
            Jobs = new JobStore(_jobs);
            Tools = new ToolStore(_tools);
            Agents = new AgentStore(_agents);
            Runs = new RunStore(_runs);
            ApiKeys = new ApiKeyStore(_apiKeys);
            Audit = new AuditStore(_audit);
        }

        public IModelStore Models { get; }
        public IAliasStore Aliases { get; }
        public IProviderConnectionStore Connections { get; }
        public IChatSessionStore Sessions { get; }
        public IDownloadStore Downloads { get; }
        public IKnowledgeStore Knowledge { get; }
        public IJobStore Jobs { get; }
        public IToolStore Tools { get; }
        public IAgentStore Agents { get; }
        public IRunStore Runs { get; }
        public IApiKeyStore ApiKeys { get; }
        public IAuditStore Audit { get; }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    private sealed class ApiKeyStore(ConcurrentDictionary<string, ApiKey> d) : IApiKeyStore
    {
        public Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ApiKey>>(d.Values.OrderBy(k => k.Name, StringComparer.Ordinal).ToList());
        public Task<ApiKey?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(d.GetValueOrDefault(id));
        public Task<ApiKey?> FindByHashAsync(string hash, CancellationToken ct = default) => Task.FromResult(d.Values.FirstOrDefault(k => string.Equals(k.Hash, hash, StringComparison.Ordinal)));
        public Task UpsertAsync(ApiKey key, CancellationToken ct = default) { d[key.Id] = key; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken ct = default) { d.TryRemove(id, out _); return Task.CompletedTask; }
    }

    private sealed class AgentStore(ConcurrentDictionary<string, AgentDefinition> d) : IAgentStore
    {
        public Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentDefinition>>(d.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList());
        public Task<AgentDefinition?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(d.GetValueOrDefault(id));
        public Task UpsertAsync(AgentDefinition agent, CancellationToken ct = default) { d[agent.Id] = agent; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken ct = default) { d.TryRemove(id, out _); return Task.CompletedTask; }
    }

    private sealed class RunStore(ConcurrentDictionary<string, RunTrace> d) : IRunStore
    {
        public Task<IReadOnlyList<RunTrace>> ListAsync(string? agentId = null, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunTrace>>(d.Values
                .Where(r => agentId is null || r.AgentId == agentId)
                .OrderByDescending(r => r.StartedAt)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList());
        public Task<RunTrace?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(d.GetValueOrDefault(id));
        public Task UpsertAsync(RunTrace run, CancellationToken ct = default) { d[run.Id] = run; return Task.CompletedTask; }

        public Task<IReadOnlyList<RunTrace>> QueryAsync(RunQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunTrace>>([.. Match(query)
                .OrderByDescending(r => r.StartedAt)
                .Skip(Math.Max(0, query.Offset))
                .Take(Math.Clamp(query.Limit, 1, 500))]);

        public Task<int> CountAsync(RunQuery query, CancellationToken ct = default) =>
            Task.FromResult(Match(query).Count());

        public Task<UsageSummary> SummariseAsync(RunQuery query, CancellationToken ct = default)
        {
            var runs = Match(query).ToList();
            var elapsed = runs.Select(r => r.ElapsedMs).Order().ToList();

            return Task.FromResult(new UsageSummary(runs.Count, runs.Count(r => !r.Success))
            {
                InputTokens = runs.Sum(r => (long?)r.InputTokens ?? 0),
                OutputTokens = runs.Sum(r => (long?)r.OutputTokens ?? 0),
                Cost = runs.Sum(r => r.EstimatedCost ?? 0),
                MedianElapsedMs = elapsed.Count == 0 ? 0 : elapsed[elapsed.Count / 2],
                Unmeasured = runs.Count(r => r.InputTokens is null && r.OutputTokens is null),
                ByAgent = Group(runs, r => r.AgentId),
                ByModel = Group(runs, r => r.ModelId ?? "(not recorded)"),
                ByUser = Group(runs, r => r.UserId ?? "(not signed in)"),
                ByDay = Group(runs, r => r.StartedAt.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            });
        }

        private IEnumerable<RunTrace> Match(RunQuery query) =>
            d.Values
                .Where(r => query.AgentId is not { Length: > 0 } || r.AgentId == query.AgentId)
                .Where(r => query.ModelId is not { Length: > 0 } || r.ModelId == query.ModelId)
                .Where(r => query.UserId is not { Length: > 0 } || r.UserId == query.UserId)
                .Where(r => query.Success is not { } success || r.Success == success)
                .Where(r => query.Since is not { } since || r.StartedAt >= since)
                .Where(r => query.Until is not { } until || r.StartedAt <= until)
                .Where(r => query.Search is not { Length: > 0 } search
                    || (r.Input?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (r.Output?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

        private static List<UsageBreakdown> Group(List<RunTrace> runs, Func<RunTrace, string> key) =>
            [.. runs.GroupBy(key)
                .Select(g => new UsageBreakdown(g.Key, g.Count())
                {
                    Tokens = g.Sum(r => (long?)r.InputTokens ?? 0) + g.Sum(r => (long?)r.OutputTokens ?? 0),
                    Cost = g.Sum(r => r.EstimatedCost ?? 0),
                })
                .OrderByDescending(b => b.Tokens)
                .ThenBy(b => b.Key, StringComparer.Ordinal)];
        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
        {
            var stale = d.Values.Where(r => r.StartedAt < olderThan).Select(r => r.Id).ToList();
            foreach (var id in stale)
            {
                d.TryRemove(id, out _);
            }

            return Task.FromResult(stale.Count);
        }
    }

    private sealed class AuditStore(ConcurrentDictionary<string, NetCoreAI.Security.AuditEntry> d) : IAuditStore
    {
        public Task<IReadOnlyList<NetCoreAI.Security.AuditEntry>> ListAsync(NetCoreAI.Security.AuditFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NetCoreAI.Security.AuditEntry>>(d.Values
                .Where(a => filter.EntityType is not { Length: > 0 } || a.EntityType == filter.EntityType)
                .Where(a => filter.EntityId is not { Length: > 0 } || a.EntityId == filter.EntityId)
                .Where(a => filter.ActorId is not { Length: > 0 } || a.ActorId == filter.ActorId)
                .Where(a => filter.Action is not { Length: > 0 } || a.Action == filter.Action)
                .Where(a => filter.Since is not { } since || a.At >= since)
                .OrderByDescending(a => a.At)
                .Take(Math.Clamp(filter.Limit, 1, 1000))
                .ToList());

        public Task WriteAsync(NetCoreAI.Security.AuditEntry entry, CancellationToken ct = default)
        {
            d[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
        {
            var stale = d.Values.Where(a => a.At < olderThan).Select(a => a.Id).ToList();
            foreach (var id in stale)
            {
                d.TryRemove(id, out _);
            }

            return Task.FromResult(stale.Count);
        }
    }

    private sealed class ToolStore(ConcurrentDictionary<string, ToolDefinition> d) : IToolStore
    {
        public Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolDefinition>>(d.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
        public Task<ToolDefinition?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(d.GetValueOrDefault(id));
        public Task<ToolDefinition?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult(d.Values.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal)));
        public Task UpsertAsync(ToolDefinition tool, CancellationToken ct = default) { d[tool.Id] = tool; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken ct = default) { d.TryRemove(id, out _); return Task.CompletedTask; }
    }

    private sealed class ModelStore(ConcurrentDictionary<string, ModelDescriptor> d) : IModelStore
    {
        public Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelDescriptor>>(d.Values.OrderBy(m => m.Name).ToList());
        public Task<ModelDescriptor?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(d.GetValueOrDefault(id));
        public Task UpsertAsync(ModelDescriptor model, CancellationToken ct = default) { d[model.Id] = model; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken ct = default) { d.TryRemove(id, out _); return Task.CompletedTask; }
    }

    private sealed class AliasStore(ConcurrentDictionary<string, ModelAlias> d) : IAliasStore
    {
        public Task<IReadOnlyList<ModelAlias>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelAlias>>(d.Values.ToList());
        public Task UpsertAsync(ModelAlias alias, CancellationToken ct = default) { d[alias.Alias] = alias; return Task.CompletedTask; }
        public Task DeleteAsync(string alias, CancellationToken ct = default) { d.TryRemove(alias, out _); return Task.CompletedTask; }
    }

    private sealed class ConnectionStore(ConcurrentDictionary<string, ProviderConnection> d) : IProviderConnectionStore
    {
        public Task<IReadOnlyList<ProviderConnection>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderConnection>>(d.Values.OrderBy(c => c.Name).ToList());
        public Task<ProviderConnection?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(d.GetValueOrDefault(id));
        public Task UpsertAsync(ProviderConnection c, CancellationToken ct = default) { d[c.Id] = c; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken ct = default) { d.TryRemove(id, out _); return Task.CompletedTask; }
    }

    private sealed class SessionStore(ConcurrentDictionary<string, ChatSession> s, ConcurrentDictionary<string, List<ChatMessageRecord>> m) : IChatSessionStore
    {
        public Task<IReadOnlyList<ChatSession>> ListAsync(string? userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatSession>>(s.Values.Where(x => userId is null || x.UserId == userId).OrderByDescending(x => x.UpdatedAt).ToList());
        public Task<ChatSession?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(s.GetValueOrDefault(id));
        public Task UpsertAsync(ChatSession session, CancellationToken ct = default) { s[session.Id] = session; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken ct = default) { s.TryRemove(id, out _); m.TryRemove(id, out _); return Task.CompletedTask; }
        public Task<IReadOnlyList<ChatMessageRecord>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            var list = m.GetValueOrDefault(sessionId);
            IReadOnlyList<ChatMessageRecord> result = list is null ? [] : list.ToList();
            return Task.FromResult(result);
        }
        public Task AppendMessageAsync(ChatMessageRecord message, CancellationToken ct = default)
        {
            var list = m.GetOrAdd(message.SessionId, _ => []);
            lock (list) { list.Add(message); }
            return Task.CompletedTask;
        }
        public Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<ChatMessageRecord> messages, CancellationToken ct = default)
        {
            m[sessionId] = [.. messages];
            return Task.CompletedTask;
        }
    }

    private sealed class SettingsStore(ConcurrentDictionary<string, string> d) : ISettingsStore
    {
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase));
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(d.GetValueOrDefault(key));
        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            if (value is null) { d.TryRemove(key, out _); } else { d[key] = value; }
            return Task.CompletedTask;
        }
    }

    private sealed class DownloadStore(ConcurrentDictionary<string, DownloadJob> d) : IDownloadStore
    {
        public Task<IReadOnlyList<DownloadJob>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DownloadJob>>(d.Values.OrderByDescending(j => j.CreatedAt).ToList());
        public Task<DownloadJob?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(d.GetValueOrDefault(id));
        public Task UpsertAsync(DownloadJob job, CancellationToken ct = default) { d[job.Id] = job; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken ct = default) { d.TryRemove(id, out _); return Task.CompletedTask; }
    }

    private sealed class KnowledgeStore(
        ConcurrentDictionary<string, KnowledgeBase> bases,
        ConcurrentDictionary<string, DataSourceDefinition> sources,
        ConcurrentDictionary<string, KnowledgeDocument> documents) : IKnowledgeStore
    {
        public Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeBase>>([.. bases.Values.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)]);

        public Task<KnowledgeBase?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(bases.GetValueOrDefault(id));

        public Task UpsertAsync(KnowledgeBase knowledgeBase, CancellationToken ct = default)
        {
            bases[knowledgeBase.Id] = knowledgeBase;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            bases.TryRemove(id, out _);

            // Sources and documents belong to the base; leaving them would orphan rows nothing can reach.
            foreach (var source in sources.Values.Where(s => s.KnowledgeBaseId == id).ToList())
            {
                sources.TryRemove(source.Id, out _);
            }

            foreach (var document in documents.Values.Where(d => d.KnowledgeBaseId == id).ToList())
            {
                documents.TryRemove(document.Id, out _);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DataSourceDefinition>> ListSourcesAsync(string knowledgeBaseId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DataSourceDefinition>>([.. sources.Values.Where(s => s.KnowledgeBaseId == knowledgeBaseId).OrderBy(s => s.CreatedAt)]);

        public Task<DataSourceDefinition?> GetSourceAsync(string id, CancellationToken ct = default) => Task.FromResult(sources.GetValueOrDefault(id));

        public Task UpsertSourceAsync(DataSourceDefinition source, CancellationToken ct = default)
        {
            sources[source.Id] = source;
            return Task.CompletedTask;
        }

        public Task DeleteSourceAsync(string id, CancellationToken ct = default)
        {
            sources.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string knowledgeBaseId, string? dataSourceId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocument>>([.. documents.Values
                .Where(d => d.KnowledgeBaseId == knowledgeBaseId && (dataSourceId is null || d.DataSourceId == dataSourceId))
                .OrderByDescending(d => d.IngestedAt)]);

        public Task<KnowledgeDocument?> GetDocumentAsync(string id, CancellationToken ct = default) => Task.FromResult(documents.GetValueOrDefault(id));

        public Task<KnowledgeDocument?> FindByHashAsync(string knowledgeBaseId, string contentHash, CancellationToken ct = default) =>
            Task.FromResult(documents.Values.FirstOrDefault(d => d.KnowledgeBaseId == knowledgeBaseId && d.ContentHash == contentHash));

        public Task UpsertDocumentAsync(KnowledgeDocument document, CancellationToken ct = default)
        {
            documents[document.Id] = document;
            return Task.CompletedTask;
        }

        public Task DeleteDocumentAsync(string id, CancellationToken ct = default)
        {
            documents.TryRemove(id, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class JobStore(ConcurrentDictionary<string, JobRecord> jobs) : IJobStore
    {
        public Task<IReadOnlyList<JobRecord>> ListAsync(string? targetId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobRecord>>([.. jobs.Values
                .Where(j => targetId is null || j.TargetId == targetId)
                .OrderByDescending(j => j.CreatedAt)]);

        public Task<JobRecord?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(jobs.GetValueOrDefault(id));

        public Task UpsertAsync(JobRecord job, CancellationToken ct = default)
        {
            jobs[job.Id] = job;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            jobs.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
        {
            var stale = jobs.Values.Where(j => j.IsTerminal && j.CreatedAt < olderThan).ToList();
            foreach (var job in stale)
            {
                jobs.TryRemove(job.Id, out _);
            }

            return Task.FromResult(stale.Count);
        }
    }
}
