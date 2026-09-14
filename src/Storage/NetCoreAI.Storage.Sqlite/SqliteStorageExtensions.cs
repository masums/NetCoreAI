using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NetCoreAI.Storage.Sqlite;

namespace NetCoreAI;

public static class SqliteStorageExtensions
{
    /// <summary>
    /// Stores NetCoreAI metadata in a SQLite file. Default location: {DataDirectory}/netcoreai.db.
    /// The NetCoreAI meta-package calls this for you; call it explicitly to pass a custom path or connection string.
    /// </summary>
    public static NetCoreAIBuilder AddSqliteStorage(this NetCoreAIBuilder builder, string? connectionStringOrPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddDbContextFactory<NetCoreAIDbContext>((sp, o) =>
        {
            var options = sp.GetRequiredService<IOptions<NetCoreAIOptions>>().Value;
            var cs = connectionStringOrPath switch
            {
                null => $"Data Source={Path.Combine(options.DataDirectory, "netcoreai.db")}",
                var s when s.Contains('=', StringComparison.Ordinal) => s,
                var path => $"Data Source={(Path.IsPathRooted(path) ? path : Path.Combine(options.DataDirectory, path))}",
            };
            o.UseSqlite(cs);
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<IMetadataStore, SqliteMetadataStore>());
        return builder;
    }
}
