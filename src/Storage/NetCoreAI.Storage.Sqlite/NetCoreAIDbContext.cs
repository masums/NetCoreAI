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
    }
}

// Rows keep a JSON payload plus the handful of columns we filter/sort on.
public sealed class ModelRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Format { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string? ConnectionId { get; set; }
    public string Json { get; set; } = "";
}

public sealed class AliasRow
{
    public string Alias { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string FallbacksJson { get; set; } = "[]";
}

public sealed class ConnectionRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Json { get; set; } = "";
}

public sealed class SessionRow
{
    public string Id { get; set; } = "";
    public string? UserId { get; set; }
    public long UpdatedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

public sealed class MessageRow
{
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

public sealed class DownloadRow
{
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

public sealed class KnowledgeBaseRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public long CreatedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

public sealed class DataSourceRow
{
    public string Id { get; set; } = "";
    public string KnowledgeBaseId { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public string Json { get; set; } = "";
}

public sealed class DocumentRow
{
    public string Id { get; set; } = "";
    public string KnowledgeBaseId { get; set; } = "";
    public string? DataSourceId { get; set; }
    public string Title { get; set; } = "";

    /// <summary>SHA-256 of the extracted text: how re-ingest decides a document has not changed.</summary>
    public string? ContentHash { get; set; }
    public long IngestedAtTicks { get; set; }
    public string Json { get; set; } = "";
}

public sealed class ToolRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Json { get; set; } = "";
}

public sealed class JobRow
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? TargetId { get; set; }
    public string State { get; set; } = "";
    public long CreatedAtTicks { get; set; }
    public string Json { get; set; } = "";
}
