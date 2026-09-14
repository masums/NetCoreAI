using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Storage.Sqlite;

/// <summary>SQLite metadata store at {DataDirectory}/netcoreai.db (WAL mode). Zero configuration.</summary>
public sealed class SqliteMetadataStore : IMetadataStore
{
    private readonly IDbContextFactory<NetCoreAIDbContext> _factory;
    private readonly ILogger<SqliteMetadataStore> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public SqliteMetadataStore(IDbContextFactory<NetCoreAIDbContext> factory, ILogger<SqliteMetadataStore> logger)
    {
        _factory = factory;
        _logger = logger;
        Models = new ModelStore(factory);
        Aliases = new AliasStore(factory);
        Connections = new ConnectionStore(factory);
        Sessions = new SessionStore(factory);
        Settings = new SettingsStore(factory);
        Downloads = new DownloadStore(factory);
        Knowledge = new SqliteKnowledgeStore(factory);
        Jobs = new SqliteJobStore(factory);
        Tools = new SqliteToolStore(factory);
        Agents = new SqliteAgentStore(factory);
        Runs = new SqliteRunStore(factory);
    }

    public IModelStore Models { get; }
    public IAliasStore Aliases { get; }
    public IProviderConnectionStore Connections { get; }
    public IChatSessionStore Sessions { get; }
    public ISettingsStore Settings { get; }
    public IDownloadStore Downloads { get; }
    public IKnowledgeStore Knowledge { get; }
    public IJobStore Jobs { get; }

    public IToolStore Tools { get; }

    public IAgentStore Agents { get; }

    public IRunStore Runs { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Schema is created from the model; migrations are introduced once the schema is frozen (pre-1.0 we recreate on breaking change).
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

        // Multi-instance detection (ADR-0003).
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5).UtcTicks;
        db.Instances.Add(new InstanceRow { Id = _instanceId, MachineName = Environment.MachineName, LastSeenAtTicks = DateTimeOffset.UtcNow.UtcTicks });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var others = await db.Instances.CountAsync(i => i.Id != _instanceId && i.LastSeenAtTicks > cutoff, cancellationToken).ConfigureAwait(false);
        if (others > 0)
        {
            _logger.LogWarning("{Count} other NetCoreAI instance(s) wrote to this SQLite store in the last 5 minutes. SQLite is not shared-safe across hosts; use NetCoreAI.Storage.SqlServer/Postgres for load-balanced deployments.", others);
        }

        var stale = DateTimeOffset.UtcNow.AddDays(-1).UtcTicks;
        await db.Instances.Where(i => i.LastSeenAtTicks < stale).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static string Ser<T>(T value) => Serialize(value);
    private static T De<T>(string json) => Deserialize<T>(json);

    // Shared with the knowledge and job stores, which live in their own file.
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, NetCoreAIDbContext.Json);
    internal static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, NetCoreAIDbContext.Json)!;

    private sealed class ModelStore(IDbContextFactory<NetCoreAIDbContext> f) : IModelStore
    {
        public async Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Models.AsNoTracking().OrderBy(m => m.Name).Select(m => m.Json).ToListAsync(ct).ConfigureAwait(false);
            return rows.Select(De<ModelDescriptor>).ToList();
        }

        public async Task<ModelDescriptor?> GetAsync(string id, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var json = await db.Models.AsNoTracking().Where(m => m.Id == id).Select(m => m.Json).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return json is null ? null : De<ModelDescriptor>(json);
        }

        public async Task UpsertAsync(ModelDescriptor model, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Models.FindAsync([model.Id], ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new ModelRow { Id = model.Id };
                db.Models.Add(row);
            }

            row.Name = model.Name;
            row.Format = model.Format.ToString();
            row.ProviderId = model.ProviderId;
            row.ConnectionId = model.ConnectionId;
            row.Json = Ser(model);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string id, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Models.Where(m => m.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class AliasStore(IDbContextFactory<NetCoreAIDbContext> f) : IAliasStore
    {
        public async Task<IReadOnlyList<ModelAlias>> ListAsync(CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Aliases.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            return rows.Select(r => new ModelAlias(r.Alias, r.ModelId, De<List<string>>(r.FallbacksJson))).ToList();
        }

        public async Task UpsertAsync(ModelAlias alias, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Aliases.FindAsync([alias.Alias], ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new AliasRow { Alias = alias.Alias };
                db.Aliases.Add(row);
            }

            row.ModelId = alias.ModelId;
            row.FallbacksJson = Ser(alias.FallbackModelIds);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string alias, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Aliases.Where(a => a.Alias == alias).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class ConnectionStore(IDbContextFactory<NetCoreAIDbContext> f) : IProviderConnectionStore
    {
        public async Task<IReadOnlyList<ProviderConnection>> ListAsync(CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Connections.AsNoTracking().OrderBy(c => c.Name).Select(c => c.Json).ToListAsync(ct).ConfigureAwait(false);
            return rows.Select(De<ProviderConnection>).ToList();
        }

        public async Task<ProviderConnection?> GetAsync(string id, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var json = await db.Connections.AsNoTracking().Where(c => c.Id == id).Select(c => c.Json).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return json is null ? null : De<ProviderConnection>(json);
        }

        public async Task UpsertAsync(ProviderConnection c, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Connections.FindAsync([c.Id], ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new ConnectionRow { Id = c.Id };
                db.Connections.Add(row);
            }

            row.Name = c.Name;
            row.ProviderId = c.ProviderId;
            row.Json = Ser(c);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string id, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Connections.Where(c => c.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class SessionStore(IDbContextFactory<NetCoreAIDbContext> f) : IChatSessionStore
    {
        public async Task<IReadOnlyList<ChatSession>> ListAsync(string? userId, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var q = db.Sessions.AsNoTracking();
            if (userId is not null)
            {
                q = q.Where(s => s.UserId == userId);
            }

            var rows = await q.OrderByDescending(s => s.UpdatedAtTicks).Select(s => s.Json).ToListAsync(ct).ConfigureAwait(false);
            return rows.Select(De<ChatSession>).ToList();
        }

        public async Task<ChatSession?> GetAsync(string id, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var json = await db.Sessions.AsNoTracking().Where(s => s.Id == id).Select(s => s.Json).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return json is null ? null : De<ChatSession>(json);
        }

        public async Task UpsertAsync(ChatSession session, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Sessions.FindAsync([session.Id], ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new SessionRow { Id = session.Id };
                db.Sessions.Add(row);
            }

            row.UserId = session.UserId;
            row.UpdatedAtTicks = session.UpdatedAt.UtcTicks;
            row.Json = Ser(session);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string id, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Messages.Where(m => m.SessionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.Sessions.Where(s => s.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<ChatMessageRecord>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Messages.AsNoTracking().Where(m => m.SessionId == sessionId).OrderBy(m => m.CreatedAtTicks).Select(m => m.Json).ToListAsync(ct).ConfigureAwait(false);
            return rows.Select(De<ChatMessageRecord>).ToList();
        }

        public async Task AppendMessageAsync(ChatMessageRecord message, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.Messages.Add(new MessageRow { Id = message.Id, SessionId = message.SessionId, CreatedAtTicks = message.CreatedAt.UtcTicks, Json = Ser(message) });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async Task ReplaceMessagesAsync(string sessionId, IReadOnlyList<ChatMessageRecord> messages, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await db.Messages.Where(m => m.SessionId == sessionId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.Messages.AddRange(messages.Select(m => new MessageRow { Id = m.Id, SessionId = sessionId, CreatedAtTicks = m.CreatedAt.UtcTicks, Json = Ser(m) }));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class SettingsStore(IDbContextFactory<NetCoreAIDbContext> f) : ISettingsStore
    {
        public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, ct).ConfigureAwait(false);
        }

        public async Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        public async Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (value is null)
            {
                await db.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                return;
            }

            var row = await db.Settings.FindAsync([key], ct).ConfigureAwait(false);
            if (row is null)
            {
                db.Settings.Add(new SettingRow { Key = key, Value = value });
            }
            else
            {
                row.Value = value;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class DownloadStore(IDbContextFactory<NetCoreAIDbContext> f) : IDownloadStore
    {
        public async Task<IReadOnlyList<DownloadJob>> ListAsync(CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Downloads.AsNoTracking().OrderByDescending(d => d.CreatedAtTicks).Select(d => d.Json).ToListAsync(ct).ConfigureAwait(false);
            return rows.Select(De<DownloadJob>).ToList();
        }

        public async Task<DownloadJob?> GetAsync(string id, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var json = await db.Downloads.AsNoTracking().Where(d => d.Id == id).Select(d => d.Json).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return json is null ? null : De<DownloadJob>(json);
        }

        public async Task UpsertAsync(DownloadJob job, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Downloads.FindAsync([job.Id], ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new DownloadRow { Id = job.Id, CreatedAtTicks = job.CreatedAt.UtcTicks };
                db.Downloads.Add(row);
            }

            row.State = job.State.ToString();
            row.Json = Ser(job);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string id, CancellationToken ct = default)
        {
            await using var db = await f.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Downloads.Where(d => d.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }
}
