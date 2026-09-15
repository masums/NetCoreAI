using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace NetCoreAI.Storage.Sqlite;

/// <summary>
/// EF Core model for the metadata store. Complex values are stored as JSON columns so the schema stays stable
/// while records gain properties; only queried columns are first-class.
/// </summary>
public sealed class NetCoreAIDbContext(DbContextOptions<NetCoreAIDbContext> options) : DbContext(options)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The tenant every query is filtered to and every new row is stamped with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An instance property rather than a captured field, because the model is built once and cached: a
    /// field would bake the first tenant's id into the filter for every tenant after it. EF parameterises
    /// a member access on the context and re-reads it per query, which is the whole trick — and it is also
    /// what makes this safe under context pooling, where one instance serves many requests.
    /// </para>
    /// <para>
    /// The accessor is pulled off the application's service provider rather than taken as a constructor
    /// parameter, because a pooled context must have exactly one constructor taking only its options.
    /// </para>
    /// <para>
    /// No accessor means no tenancy in this host, which is the default tenant — the same value existing
    /// rows already carry, so there is no separate un-tenanted code path to get wrong.
    /// </para>
    /// </remarks>
    public string CurrentTenant =>
        (_tenants ??= Accessor(options) ?? Untenanted.Instance).Current;

    private NetCoreAI.Tenancy.ITenantAccessor? _tenants;

    private static NetCoreAI.Tenancy.ITenantAccessor? Accessor(DbContextOptions options) =>
        options.FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()
            ?.ApplicationServiceProvider
            ?.GetService(typeof(NetCoreAI.Tenancy.ITenantAccessor)) as NetCoreAI.Tenancy.ITenantAccessor;

    /// <summary>Stands in when a host has no tenancy at all, so the filter has something to read.</summary>
    private sealed class Untenanted : NetCoreAI.Tenancy.ITenantAccessor
    {
        public static readonly Untenanted Instance = new();

        public string Current => NetCoreAI.Tenancy.TenantId.Default;

        public IDisposable Use(string tenantId) =>
            throw new NotSupportedException("This host has no tenancy configured.");
    }

    public DbSet<ModelRow> Models => Set<ModelRow>();
    public DbSet<AliasRow> Aliases => Set<AliasRow>();
    public DbSet<ConnectionRow> Connections => Set<ConnectionRow>();
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<MessageRow> Messages => Set<MessageRow>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();
    public DbSet<DownloadRow> Downloads => Set<DownloadRow>();
    public DbSet<InstanceRow> Instances => Set<InstanceRow>();
    public DbSet<KnowledgeBaseRow> KnowledgeBases => Set<KnowledgeBaseRow>();
    public DbSet<DataSourceRow> DataSources => Set<DataSourceRow>();
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();
    public DbSet<JobRow> Jobs => Set<JobRow>();
    public DbSet<ToolRow> Tools => Set<ToolRow>();
    public DbSet<AgentRow> Agents => Set<AgentRow>();
    public DbSet<RunRow> Runs => Set<RunRow>();
    public DbSet<ApiKeyRow> ApiKeys => Set<ApiKeyRow>();
    public DbSet<AuditRow> Audit => Set<AuditRow>();
    public DbSet<AgentVersionRow> AgentVersions => Set<AgentVersionRow>();

    /// <summary>
    /// Stamps new rows with the current tenant.
    /// </summary>
    /// <remarks>
    /// Done here rather than in each store so that a store cannot forget. Always overwritten rather than
    /// filled in when blank: a row is created by whoever is acting now, and letting a caller choose would
    /// make writing into another tenant a matter of setting a field.
    /// </remarks>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.TenantId = CurrentTenant;
            }
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Filters every tenant-owned table to the current tenant, and gives each one its column.
    /// </summary>
    /// <remarks>
    /// A filter here covers every read in every store — including <c>Find</c>, <c>ExecuteDelete</c> and
    /// <c>ExecuteUpdate</c>, which all go through the same queryable. The column defaults to the default
    /// tenant so a database written before tenancy existed reads as one tenant's data rather than as
    /// nobody's.
    /// </remarks>
    private void ConfigureTenancy(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<AliasRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<ConnectionRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<SessionRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<MessageRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<KnowledgeBaseRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<DataSourceRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<DocumentRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<ToolRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<AgentRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<RunRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<ApiKeyRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<AuditRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<AgentVersionRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<DownloadRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);
        modelBuilder.Entity<JobRow>().HasQueryFilter(r => r.TenantId == CurrentTenant);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entity.ClrType))
            {
                continue;
            }

            modelBuilder.Entity(entity.ClrType)
                .Property(nameof(ITenantOwned.TenantId))
                .IsRequired()

                // The default is what lets this column be added to an existing table: every row already
                // there becomes the default tenant's, which is what it always was.
                .HasDefaultValue(NetCoreAI.Tenancy.TenantId.Default);

            // The tenant is part of the identity of the row, not a column beside it. Two tenants both
            // calling an agent "support" is the ordinary case, and a key of Id alone makes the second one
            // a UNIQUE constraint failure — at save time, in front of a user, for no reason they can see.
            //
            // It also means every key lookup is scoped by construction rather than by remembering to
            // filter, which is the stronger guarantee of the two.
            var key = entity.FindPrimaryKey()?.Properties.Select(p => p.Name).ToList() ?? ["Id"];
            if (key is not [nameof(ITenantOwned.TenantId), ..])
            {
                modelBuilder.Entity(entity.ClrType).HasKey([nameof(ITenantOwned.TenantId), .. key]);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelRow>(e =>
        {
            e.ToTable("Models");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ConnectionId);
            e.Property(x => x.Json).IsRequired();
        });
        modelBuilder.Entity<AliasRow>(e => { e.ToTable("Aliases"); e.HasKey(x => x.Alias); });
        modelBuilder.Entity<ConnectionRow>(e => { e.ToTable("ProviderConnections"); e.HasKey(x => x.Id); });
        modelBuilder.Entity<SessionRow>(e =>
        {
            e.ToTable("Sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.UpdatedAtTicks });
        });
        modelBuilder.Entity<MessageRow>(e =>
        {
            e.ToTable("Messages");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SessionId, x.CreatedAtTicks });
        });
        modelBuilder.Entity<SettingRow>(e => { e.ToTable("Settings"); e.HasKey(x => x.Key); });
        modelBuilder.Entity<DownloadRow>(e => { e.ToTable("Downloads"); e.HasKey(x => x.Id); });
        modelBuilder.Entity<InstanceRow>(e => { e.ToTable("Instances"); e.HasKey(x => x.Id); });
        modelBuilder.Entity<KnowledgeBaseRow>(e => { e.ToTable("KnowledgeBases"); e.HasKey(x => x.Id); });
        modelBuilder.Entity<DataSourceRow>(e =>
        {
            e.ToTable("DataSources");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.KnowledgeBaseId);
        });
        modelBuilder.Entity<DocumentRow>(e =>
        {
            e.ToTable("Documents");
            e.HasKey(x => x.Id);

            // Re-ingest checks "have I seen this content already?" per base, which is this index.
            e.HasIndex(x => new { x.KnowledgeBaseId, x.ContentHash });
            e.HasIndex(x => new { x.KnowledgeBaseId, x.DataSourceId });
        });
        modelBuilder.Entity<JobRow>(e =>
        {
            e.ToTable("Jobs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TargetId, x.CreatedAtTicks });
            e.HasIndex(x => x.State);
        });
        modelBuilder.Entity<ToolRow>(e =>
        {
            e.ToTable("Tools");
            e.HasKey(x => x.Id);

            // The model calls a tool by name, so two tools sharing one would make a call ambiguous.
            e.HasIndex(x => x.Name).IsUnique();
        });
        modelBuilder.Entity<AgentRow>(e =>
        {
            e.ToTable("Agents");
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<ApiKeyRow>(e =>
        {
            e.ToTable("ApiKeys");
            e.HasKey(x => x.Id);

            // Every authenticated call looks a key up by hash, so this index is on the hot path.
            e.HasIndex(x => x.Hash).IsUnique();
        });
        modelBuilder.Entity<AuditRow>(e =>
        {
            e.ToTable("Audit");
            e.HasKey(x => x.Id);

            // The two questions an audit log is opened to answer: what happened lately, and what happened
            // to this one thing.
            e.HasIndex(x => x.AtTicks);
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.AtTicks });
            e.HasIndex(x => new { x.ActorId, x.AtTicks });
        });
        modelBuilder.Entity<AgentVersionRow>(e =>
        {
            e.ToTable("AgentVersions");

            // The agent and the number together. A version means nothing without the agent it is of, and
            // two agents both having a version 3 is the ordinary case.
            e.HasKey(x => new { x.AgentId, x.Version });
            e.HasIndex(x => new { x.AgentId, x.Version });
        });
        modelBuilder.Entity<RunRow>(e =>
        {
            e.ToTable("Runs");
            e.HasKey(x => x.Id);

            // Runs are listed newest-first for one agent, and pruned by age.
            e.HasIndex(x => new { x.AgentId, x.StartedAtTicks });
            e.HasIndex(x => x.StartedAtTicks);

            // Usage is read per model and per person as often as per agent.
            e.HasIndex(x => new { x.ModelId, x.StartedAtTicks });
            e.HasIndex(x => new { x.UserId, x.StartedAtTicks });
        });

        ConfigureTenancy(modelBuilder);
    }
}

// Rows keep a JSON payload plus the handful of columns we filter/sort on.
/// <summary>
/// A row that belongs to one tenant.
/// </summary>
/// <remarks>
/// The interface exists so the query filter and the stamp can be written once, in
/// <see cref="NetCoreAIDbContext"/>, rather than in each of the fourteen stores — where the one that
/// forgot would be a data leak rather than a compile error.
/// </remarks>
public interface ITenantOwned
{
    string TenantId { get; set; }
}

public sealed class ModelRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Format { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string? ConnectionId { get; set; }
    public string Json { get; set; } = "";
}

public sealed class AliasRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Alias { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string FallbacksJson { get; set; } = "[]";
}

public sealed class ConnectionRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Json { get; set; } = "";
}

public sealed class SessionRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string? UserId { get; set; }
    public long UpdatedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

public sealed class MessageRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long CreatedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

public sealed class SettingRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Scope { get; set; } = "Global";
}

public sealed class DownloadRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string State { get; set; } = "";
    public long CreatedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

/// <summary>Heartbeat per host instance; several live rows on one SQLite file mean a multi-instance deployment (ADR-0003 warning).</summary>
public sealed class InstanceRow
{
    public string Id { get; set; } = "";
    public string MachineName { get; set; } = "";
    public long LastSeenAtTicks { get; set; }
}

public sealed class KnowledgeBaseRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public long CreatedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

public sealed class DataSourceRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string KnowledgeBaseId { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public string Json { get; set; } = "";
}

public sealed class DocumentRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string KnowledgeBaseId { get; set; } = "";
    public string? DataSourceId { get; set; }
    public string Title { get; set; } = "";

    /// <summary>SHA-256 of the extracted text: how re-ingest decides a document has not changed.</summary>
    public string? ContentHash { get; set; }
    public long IngestedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

public sealed class ToolRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Json { get; set; } = "";
}

public sealed class AgentRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Json { get; set; } = "";
}

public sealed class ApiKeyRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Json { get; set; } = "";
}

/// <summary>
/// One audit entry. The columns are the ones a filter uses; everything else is in the JSON, like every
/// other row here.
/// </summary>
public sealed class AuditRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public long AtTicks { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? ActorId { get; set; }
    public string Json { get; set; } = "";
}

/// <summary>One published version of an agent.</summary>
public sealed class AgentVersionRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string AgentId { get; set; } = "";
    public int Version { get; set; }
    public long PublishedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

public sealed class RunRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string AgentId { get; set; } = "";
    public long StartedAtTicks { get; set; }

    /// <summary>
    /// What usage is filtered and totalled by, beside the JSON.
    /// </summary>
    /// <remarks>
    /// Nullable, every one of them, so that rows written before these columns existed read as "not
    /// recorded" rather than as zero. A run that used an unknown number of tokens is not a run that used
    /// none, and a report that quietly says otherwise is worse than one with a gap in it.
    /// </remarks>
    public string? ModelId { get; set; }

    /// <inheritdoc cref="ModelId"/>
    public string? UserId { get; set; }

    /// <inheritdoc cref="ModelId"/>
    public int? InputTokens { get; set; }

    /// <inheritdoc cref="ModelId"/>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// Estimated cost, as a double.
    /// </summary>
    /// <remarks>
    /// SQLite has no decimal type and EF stores one as text, which cannot be summed in SQL. The figure is
    /// an estimate from a per-token price, so the exactness a decimal buys was never there to lose; the
    /// authoritative per-run figure is still in the JSON.
    /// </remarks>
    public double? Cost { get; set; }

    /// <inheritdoc cref="ModelId"/>
    public bool? Success { get; set; }

    /// <inheritdoc cref="ModelId"/>
    public long? ElapsedMs { get; set; }

    public string Json { get; set; } = "";
}

public sealed class JobRow : ITenantOwned
{
    public string TenantId { get; set; } = NetCoreAI.Tenancy.TenantId.Default;
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? TargetId { get; set; }
    public string State { get; set; } = "";
    public long CreatedAtTicks { get; set; }
    public string Json { get; set; } = "";
}
