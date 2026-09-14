namespace NetCoreAI;

/// <summary>One part of an extracted document, carrying where it came from so a citation can point back.</summary>
/// <param name="Text">The text of this section.</param>
public sealed record DocumentSection(string Text)
{
    /// <summary>1-based page for paged formats (PDF, DOCX with page breaks); null otherwise.</summary>
    public int? Page { get; init; }

    /// <summary>Nearest heading above this text, when the format has headings.</summary>
    public string? Heading { get; init; }

    /// <summary>Heading depth, so a chunker can respect document structure.</summary>
    public int HeadingLevel { get; init; }

    /// <summary>Extra metadata this section carries (a table name, a row key, an HTML element id).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>A document after extraction: plain text in order, plus what the format knew about it.</summary>
public sealed record ExtractedDocument
{
    public required string Title { get; init; }

    public required IReadOnlyList<DocumentSection> Sections { get; init; }

    /// <summary>Format-level metadata: author, created date, page count.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Total characters extracted, for progress reporting and dedup.</summary>
    public int Length => Sections.Sum(s => s.Text.Length);
}

/// <summary>
/// Turns a file into text and structure. One implementation per family of formats, discovered through DI,
/// so a host can add a format without touching the pipeline.
/// </summary>
public interface IDocumentExtractor
{
    /// <summary>Ordering hint when several extractors accept a file; higher wins.</summary>
    int Priority => 0;

    /// <summary>True when this extractor handles the file, judged on extension and content type.</summary>
    bool CanHandle(string fileName, string? contentType);

    Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default);
}

/// <summary>A piece of a document, ready to embed.</summary>
/// <param name="Text">The chunk text.</param>
/// <param name="Index">Position within the document, 0-based.</param>
public sealed record DocumentChunk(string Text, int Index)
{
    public int? Page { get; init; }

    public string? Section { get; init; }

    /// <summary>Approximate token count, as the chunker measured it.</summary>
    public int TokenCount { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>Splits an extracted document into chunks. Strategies differ in what they refuse to split across.</summary>
public interface IChunker
{
    ChunkingStrategy Strategy { get; }

    IReadOnlyList<DocumentChunk> Chunk(ExtractedDocument document, ChunkingOptions options);
}

/// <summary>A document offered by a data source, fetched lazily so a large source is not held in memory.</summary>
/// <param name="Id">Stable id within the source, so re-syncing updates rather than duplicates.</param>
/// <param name="Title">Display title.</param>
public sealed record SourceDocument(string Id, string Title)
{
    /// <summary>Opens the content. Called only when the document needs (re-)ingesting.</summary>
    public required Func<CancellationToken, Task<Stream>> OpenAsync { get; init; }

    public string? FileName { get; init; }

    public string? ContentType { get; init; }

    public long? SizeBytes { get; init; }

    public string? Source { get; init; }

    /// <summary>When the source can report it cheaply, used to skip unchanged documents before fetching.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>A hash the source already knows, which saves extracting a document only to find it unchanged.</summary>
    public string? ContentHash { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public IReadOnlyList<string>? AclTags { get; init; }
}

/// <summary>Where documents come from: uploaded files, a SQL query, a REST endpoint, or the host.</summary>
public interface IDataSource
{
    /// <summary>Type discriminator stored on the definition, e.g. "files", "sql", "rest".</summary>
    string Type { get; }

    /// <summary>Enumerates the documents this source currently offers.</summary>
    IAsyncEnumerable<SourceDocument> EnumerateAsync(DataSourceDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Checks the configuration and reports what it found, so a user is not left guessing.</summary>
    Task<DataSourceTestResult> TestAsync(DataSourceDefinition definition, CancellationToken cancellationToken = default);
}

/// <summary>Result of testing a data source's configuration.</summary>
/// <param name="Success">Whether the source could be reached and read.</param>
/// <param name="Message">What happened, in words a user can act on.</param>
/// <param name="DocumentCount">Documents found, when the source can count cheaply.</param>
public sealed record DataSourceTestResult(bool Success, string Message, int? DocumentCount = null)
{
    /// <summary>A few titles, so the user can confirm it found what they expected.</summary>
    public IReadOnlyList<string> SampleTitles { get; init; } = [];
}

/// <summary>Configuration of one data source attached to a knowledge base.</summary>
public sealed record DataSourceDefinition
{
    public required string Id { get; init; }

    public required string KnowledgeBaseId { get; init; }

    public required string Name { get; init; }

    /// <summary>Matches <see cref="IDataSource.Type"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Type-specific settings. Secrets are stored protected and never returned by the API.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Cron expression for scheduled sync; null means manual only.</summary>
    public string? Schedule { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>ACL tags applied to every document from this source, on top of the base's defaults.</summary>
    public IReadOnlyList<string> AclTags { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSyncedAt { get; init; }

    public string? LastError { get; init; }
}

/// <summary>
/// Host-side push: an application that already knows its own documents can feed them in directly rather
/// than having NetCoreAI crawl them back out of a database.
/// </summary>
public interface IKnowledgeSource
{
    /// <summary>Knowledge base these documents belong to.</summary>
    string KnowledgeBaseId { get; }

    IAsyncEnumerable<SourceDocument> GetDocumentsAsync(CancellationToken cancellationToken = default);
}

/// <summary>A passage retrieved for a query.</summary>
/// <param name="Chunk">The stored chunk.</param>
/// <param name="Score">Similarity to the query.</param>
/// <param name="Citation">Where it came from, ready to show.</param>
public sealed record RetrievedChunk(VectorRecord Chunk, float Score, Citation Citation);

/// <summary>
/// The knowledge surface application code uses: ingest documents, search them, and remove them.
/// Implemented in-process and over HTTP, so the same calls work inside the host and from a client app.
/// </summary>
public interface IKnowledgeClient
{
    Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken cancellationToken = default);

    Task<KnowledgeBase?> GetAsync(string knowledgeBaseId, CancellationToken cancellationToken = default);

    /// <summary>Ingests one document immediately, returning once it is searchable.</summary>
    Task<KnowledgeDocument> IngestAsync(string knowledgeBaseId, SourceDocument document, CancellationToken cancellationToken = default);

    /// <summary>Queues a sync of every data source on the base; returns the job to watch.</summary>
    Task<string> SyncAsync(string knowledgeBaseId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string knowledgeBaseId, string query, RetrievalOptions? options = null, IReadOnlyList<string>? callerTags = null, CancellationToken cancellationToken = default);

    Task DeleteDocumentAsync(string knowledgeBaseId, string documentId, CancellationToken cancellationToken = default);
}
