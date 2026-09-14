namespace NetCoreAI;

/// <summary>How a document is split before embedding.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ChunkingStrategy>))]
public enum ChunkingStrategy
{
    /// <summary>Fixed token window with overlap. Predictable and format-agnostic.</summary>
    FixedSize,

    /// <summary>Split on structure first (headings, then paragraphs, then sentences), falling back to size.</summary>
    RecursiveStructure,

    /// <summary>Whole sentences, packed up to the size limit; keeps quotes intact.</summary>
    Sentence,

    /// <summary>One chunk per row, for tabular sources where a row is the unit of meaning.</summary>
    Row,
}

/// <summary>Chunking configuration for a knowledge base.</summary>
public sealed record ChunkingOptions
{
    public ChunkingStrategy Strategy { get; init; } = ChunkingStrategy.RecursiveStructure;

    /// <summary>Target chunk size in tokens. Smaller retrieves more precisely; larger keeps more context together.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>Tokens repeated from the previous chunk, so a fact split across a boundary is still retrievable.</summary>
    public int OverlapTokens { get; init; } = 64;

    /// <summary>Chunks shorter than this are dropped: a stray heading retrieves noise.</summary>
    public int MinTokens { get; init; } = 16;

    public static readonly ChunkingOptions Default = new();
}

/// <summary>A knowledge base: a collection of ingested documents with the settings used to build it.</summary>
public sealed record KnowledgeBase
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Model id or alias used to embed both documents and queries. Changing it invalidates the index.</summary>
    public string EmbeddingModel { get; init; } = ModelAlias.Embed;

    /// <summary>Vector dimensions, recorded when the collection is created so a model swap is caught.</summary>
    public int? Dimensions { get; init; }

    /// <summary>Id of the <see cref="IVectorStore"/> holding this base's collection.</summary>
    public string VectorStoreId { get; init; } = "sqlite";

    public ChunkingOptions Chunking { get; init; } = ChunkingOptions.Default;

    /// <summary>Default retrieval settings; a caller can override them per request.</summary>
    public RetrievalOptions Retrieval { get; init; } = RetrievalOptions.Default;

    /// <summary>ACL tags every chunk gets unless its source says otherwise. Empty means public.</summary>
    public IReadOnlyList<string> DefaultAclTags { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastIngestedAt { get; init; }

    /// <summary>Documents currently indexed, kept as a counter so the UI need not scan the store.</summary>
    public int DocumentCount { get; init; }

    public int ChunkCount { get; init; }

    /// <summary>The vector collection name for this base.</summary>
    public string Collection => $"kb_{Id}";
}

/// <summary>What a retrieval should return.</summary>
public sealed record RetrievalOptions
{
    /// <summary>Chunks to retrieve before any filtering.</summary>
    public int TopK { get; init; } = 5;

    /// <summary>Drop hits below this similarity. Null keeps everything the top-k returned.</summary>
    public float? MinScore { get; init; } = 0.35f;

    /// <summary>Metadata equality filters applied inside the store.</summary>
    public IReadOnlyDictionary<string, string>? MetadataFilter { get; init; }

    /// <summary>Restrict to these documents.</summary>
    public IReadOnlyList<string>? DocumentIds { get; init; }

    /// <summary>Include the chunk text in results. Off for counting or debugging at scale.</summary>
    public bool IncludeText { get; init; } = true;

    public static readonly RetrievalOptions Default = new();
}

/// <summary>Where a retrieved passage came from, as shown under an answer.</summary>
/// <param name="DocumentId">Document in the knowledge base.</param>
/// <param name="Title">Document title, for display.</param>
/// <param name="Snippet">The passage itself.</param>
/// <param name="Score">Similarity to the query.</param>
public sealed record Citation(string DocumentId, string Title, string Snippet, float Score)
{
    /// <summary>1-based page for paged formats; null for everything else.</summary>
    public int? Page { get; init; }

    /// <summary>Nearest heading above the passage, when the extractor found one.</summary>
    public string? Section { get; init; }

    /// <summary>Original location: a file path, URL, or table and key.</summary>
    public string? Source { get; init; }

    /// <summary>Knowledge base the passage came from, so a multi-KB answer can say which.</summary>
    public string? KnowledgeBaseId { get; init; }

    /// <summary>Index in the answer's citation list, so "[2]" in the text lines up with this entry.</summary>
    public int Ordinal { get; init; }
}

/// <summary>A document as the knowledge base knows it, independent of where it came from.</summary>
public sealed record KnowledgeDocument
{
    public required string Id { get; init; }

    public required string KnowledgeBaseId { get; init; }

    /// <summary>Data source that produced it; null for a document pushed directly by the host.</summary>
    public string? DataSourceId { get; init; }

    public required string Title { get; init; }

    /// <summary>Original location: file path, URL, or table and primary key.</summary>
    public string? Source { get; init; }

    public string? ContentType { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>SHA-256 of the extracted text. Re-ingesting an unchanged document is skipped on this.</summary>
    public string? ContentHash { get; init; }

    public int ChunkCount { get; init; }

    /// <summary>Metadata copied onto every chunk, so retrieval can filter on it.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Tags a caller must hold to retrieve this document's chunks. Empty means public.</summary>
    public IReadOnlyList<string> AclTags { get; init; } = [];

    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SourceModifiedAt { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Access tags. A chunk is visible when it is public or carries a tag the caller holds, which keeps the
/// check a set intersection rather than a policy evaluation per chunk.
/// </summary>
public static class AclTag
{
    /// <summary>Readable by anyone who can reach the knowledge base.</summary>
    public const string Public = "*";

    /// <summary>Builds a tag from a claim, e.g. <c>role:finance</c> or <c>tenant:acme</c>.</summary>
    public static string From(string claimType, string value) => $"{claimType}:{value}";

    /// <summary>True when a caller holding <paramref name="callerTags"/> may see a chunk tagged <paramref name="recordTags"/>.</summary>
    public static bool Allows(IReadOnlyList<string> recordTags, IReadOnlyList<string>? callerTags)
    {
        // Null caller tags mean "no filtering": system callers and admin tools.
        if (callerTags is null || recordTags.Count == 0 || recordTags.Contains(Public, StringComparer.Ordinal))
        {
            return true;
        }

        foreach (var tag in recordTags)
        {
            if (callerTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
