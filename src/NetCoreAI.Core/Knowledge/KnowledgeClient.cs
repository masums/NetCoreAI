namespace NetCoreAI.Knowledge;

/// <summary>
/// The in-process <see cref="IKnowledgeClient"/>: what application code injects to ingest and search
/// without knowing about jobs, vector stores or extractors. The HTTP client in NetCoreAI.Client implements
/// the same interface, so code written against it works inside the host and from a separate application.
/// </summary>
internal sealed class KnowledgeClient(IKnowledgeService knowledge, IRetriever retriever) : IKnowledgeClient
{
    public Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken cancellationToken = default) =>
        knowledge.ListAsync(cancellationToken);

    public Task<KnowledgeBase?> GetAsync(string knowledgeBaseId, CancellationToken cancellationToken = default) =>
        knowledge.GetAsync(knowledgeBaseId, cancellationToken);

    public Task<KnowledgeDocument> IngestAsync(string knowledgeBaseId, SourceDocument document, CancellationToken cancellationToken = default) =>
        knowledge.IngestAsync(knowledgeBaseId, document, dataSourceId: null, cancellationToken);

    public async Task<string> SyncAsync(string knowledgeBaseId, CancellationToken cancellationToken = default) =>
        (await knowledge.SyncAsync(knowledgeBaseId, dataSourceId: null, cancellationToken).ConfigureAwait(false)).Id;

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string knowledgeBaseId,
        string query,
        RetrievalOptions? options = null,
        IReadOnlyList<string>? callerTags = null,
        CancellationToken cancellationToken = default) =>
        retriever.SearchAsync(knowledgeBaseId, query, options, callerTags, cancellationToken);

    public Task DeleteDocumentAsync(string knowledgeBaseId, string documentId, CancellationToken cancellationToken = default) =>
        knowledge.DeleteDocumentAsync(knowledgeBaseId, documentId, cancellationToken);
}
