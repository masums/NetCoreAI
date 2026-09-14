using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Conformance;
using NetCoreAI.Storage;
using NetCoreAI.Storage.Sqlite;
using NetCoreAI.VectorStores.Sqlite;

namespace NetCoreAI.Core.Tests;

public class InMemoryMetadataStoreConformance : MetadataStoreConformanceTests
{
    protected override Task<IMetadataStore> CreateStoreAsync() => Task.FromResult<IMetadataStore>(new InMemoryMetadataStore());
}

public class SqliteMetadataStoreConformance : MetadataStoreConformanceTests
{
    private string? _path;

    protected override Task<IMetadataStore> CreateStoreAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), "netcoreai-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var options = new DbContextOptionsBuilder<NetCoreAIDbContext>().UseSqlite($"Data Source={_path}").Options;
        var factory = new PooledDbContextFactory<NetCoreAIDbContext>(options);
        return Task.FromResult<IMetadataStore>(new SqliteMetadataStore(factory, NullLogger<SqliteMetadataStore>.Instance));
    }

    public override ValueTask DisposeAsync()
    {
        SqliteConnectionCleanup.Delete(_path);
        return base.DisposeAsync();
    }
}

public class SqliteVectorStoreConformance : VectorStoreConformanceTests
{
    private string? _path;

    protected override Task<IVectorStore> CreateStoreAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), "netcoreai-tests", $"{Guid.NewGuid():N}-vec.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return Task.FromResult<IVectorStore>(new SqliteVectorStore(_path));
    }

    public override ValueTask DisposeAsync()
    {
        (Store as IDisposable)?.Dispose();
        SqliteConnectionCleanup.Delete(_path);
        return base.DisposeAsync();
    }
}

internal static class SqliteConnectionCleanup
{
    public static void Delete(string? path)
    {
        if (path is null)
        {
            return;
        }

        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}
