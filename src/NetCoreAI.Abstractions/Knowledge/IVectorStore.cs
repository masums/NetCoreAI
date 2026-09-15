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
/// <summary>
/// A store that can also find chunks by the words in them.
/// </summary>
/// <remarks>
/// Optional: a store implements it if it can, and retrieval falls back to vectors alone when it cannot.
/// Worth having because the two kinds of search fail differently — a vector search finds text that means
/// the same thing and misses an exact token like <c>ERR-4021</c> or a part number, which is precisely what
/// somebody typing that into a search box is looking for.
/// </remarks>
public interface IKeywordSearchable
{
    /// <summary>
    /// Chunks matching the words in <paramref name="query"/>, best first.
    /// </summary>
    /// <remarks>
    /// The score is the store's own and is not comparable with a vector similarity. Fusion works on the
    /// ranks rather than the scores for exactly that reason.
    /// </remarks>
    Task<IReadOnlyList<VectorSearchResult>> SearchKeywordAsync(
        string collection,
        string query,
        int topK,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A store that can hand back everything it holds, one chunk at a time.
/// </summary>
/// <remarks>
/// Optional, and what makes a migration between stores possible. A store that cannot do this can still be
/// migrated <em>into</em>; getting data out of it means re-indexing the documents, which is slower and
/// produces different vectors if the embedding model has moved on since.
/// </remarks>
public interface IVectorEnumerable
{
    /// <summary>
    /// Every chunk in a collection, streamed.
    /// </summary>
    /// <remarks>
    /// Streamed rather than returned as a list on purpose: a modest knowledge base is hundreds of
    /// thousands of chunks, each carrying a vector of a thousand floats, and a migration that held one in
    /// memory would be a migration that only worked on small ones.
    /// </remarks>
    IAsyncEnumerable<VectorRecord> ReadAllAsync(string collection, CancellationToken cancellationToken = default);
}

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
