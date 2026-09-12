using System.Collections.Concurrent;

namespace NetCoreAI.Storage;

/// <summary>
/// Non-persistent store used by tests and as the fallback when no storage package is registered
/// (a warning is logged in that case; nothing survives a restart).
/// </summary>
public sealed class InMemoryMetadataStore : IMetadataStore
{
    private readonly ConcurrentDictionary<string, ModelDescriptor> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModelAlias> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<ChatMessageRecord>> _messages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DownloadJob> _downloads = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryMetadataStore()
    {
        Models = new ModelStore(_models);
        Aliases = new AliasStore(_aliases);
        Connections = new ConnectionStore(_connections);
        Sessions = new SessionStore(_sessions, _messages);
        Settings = new SettingsStore(_settings);
        Downloads = new DownloadStore(_downloads);
    }

    public IModelStore Models { get; }
    public IAliasStore Aliases { get; }
    public IProviderConnectionStore Connections { get; }
    public IChatSessionStore Sessions { get; }
    public ISettingsStore Settings { get; }
    public IDownloadStore Downloads { get; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

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
}
