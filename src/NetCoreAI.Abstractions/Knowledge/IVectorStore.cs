namespace NetCoreAI;

/// <summary>Distance function used by a vector collection.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<VectorDistance>))]
public enum VectorDistance
{
    Cosine,
    DotProduct,
    Euclidean,
}

/// <summary>One stored chunk: id, embedding, text and filterable metadata.</summary>
public sealed record VectorRecord(string Id, string DocumentId, ReadOnlyMemory<float> Embedding, string Text, IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<string> AclTags);

/// <summary>A search hit with its similarity score (higher = closer for cosine/dot, lower = closer for euclidean).</summary>
public sealed record VectorSearchResult(VectorRecord Record, float Score);

/// <summary>Filter for a vector search. Metadata equality filters are ANDed; ACL tags: a record matches if it has the "*" tag or any tag in <see cref="CallerTags"/>.</summary>
public sealed record VectorFilter
{
    public IReadOnlyDictionary<string, string>? MetadataEquals { get; init; }
    public IReadOnlyList<string>? DocumentIds { get; init; }
    /// <summary>Null = no ACL filtering (admin/system); empty = only public records.</summary>
    public IReadOnlyList<string>? CallerTags { get; init; }
    public float? MinScore { get; init; }
}

/// <summary>
/// Storage and similarity search for embeddings. One collection per knowledge base.
/// Implementations: SQLite (default, zero-config), Postgres/pgvector, Qdrant.
/// </summary>
public interface IVectorStore
{
    string Id { get; }

    Task EnsureCollectionAsync(string collection, int dimensions, VectorDistance distance = VectorDistance.Cosine, CancellationToken cancellationToken = default);

    Task DeleteCollectionAsync(string collection, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(string collection, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken = default);

    Task DeleteByDocumentAsync(string collection, string documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string collection, ReadOnlyMemory<float> query, int topK, VectorFilter? filter = null, CancellationToken cancellationToken = default);

    Task<long> CountAsync(string collection, CancellationToken cancellationToken = default);
}
