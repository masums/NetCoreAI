using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetCoreAI.VectorStores.Sqlite;

namespace NetCoreAI;

public static class SqliteVectorStoreExtensions
{
    /// <summary>Uses the SQLite vector store at {DataDirectory}/vectors.db. Registered automatically by the NetCoreAI meta-package.</summary>
    public static NetCoreAIBuilder AddSqliteVectorStore(this NetCoreAIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IVectorStore, SqliteVectorStore>());
        return builder;
    }
}
