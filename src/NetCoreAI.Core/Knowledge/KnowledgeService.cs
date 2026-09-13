using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Knowledge;

/// <summary>
/// Knowledge bases end to end: create and configure them, attach data sources, run syncs as background
/// jobs, and delete what is no longer wanted from both the metadata store and the vector store.
/// </summary>
public interface IKnowledgeService
{
    Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken cancellationToken = default);

    Task<KnowledgeBase?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<KnowledgeBase> CreateAsync(KnowledgeBase knowledgeBase, CancellationToken cancellationToken = default);

    Task<KnowledgeBase> UpdateAsync(KnowledgeBase knowledgeBase, CancellationToken cancellationToken = default);

    /// <summary>Removes the base, its sources, its documents and its vector collection.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DataSourceDefinition>> ListSourcesAsync(string knowledgeBaseId, CancellationToken cancellationToken = default);

    Task<DataSourceDefinition> SaveSourceAsync(DataSourceDefinition source, CancellationToken cancellationToken = default);

    Task DeleteSourceAsync(string sourceId, bool deleteDocuments = true, CancellationToken cancellationToken = default);

    Task<DataSourceTestResult> TestSourceAsync(DataSourceDefinition source, CancellationToken cancellationToken = default);

    /// <summary>Queues a sync of one source, or of every enabled source on the base. Returns the job.</summary>
    Task<JobRecord> SyncAsync(string knowledgeBaseId, string? dataSourceId = null, CancellationToken cancellationToken = default);

    /// <summary>Ingests one document immediately, returning when it is searchable.</summary>
    Task<KnowledgeDocument> IngestAsync(string knowledgeBaseId, SourceDocument document, string? dataSourceId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string knowledgeBaseId, string? dataSourceId = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a document's row and its chunks, so it stops being retrievable.</summary>
    Task DeleteDocumentAsync(string knowledgeBaseId, string documentId, CancellationToken cancellationToken = default);

    /// <summary>The data source types available in this host, for the "add a source" picker.</summary>
    IReadOnlyList<string> SourceTypes { get; }
}

internal sealed class KnowledgeService(
    IMetadataStore store,
    IIngestionPipeline pipeline,
    IBackgroundJobRunner jobs,
    IEnumerable<IDataSource> dataSources,
    IEnumerable<IVectorStore> vectorStores,
    IEnumerable<IKnowledgeSource> hostSources,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<KnowledgeService> logger) : IKnowledgeService
{
    private readonly List<IDataSource> _dataSources = [.. dataSources];
    private readonly List<IVectorStore> _vectorStores = [.. vectorStores];
    private readonly List<IKnowledgeSource> _hostSources = [.. hostSources];

    public IReadOnlyList<string> SourceTypes => [.. _dataSources.Select(s => s.Type).Order(StringComparer.Ordinal)];

    public Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken cancellationToken = default) =>
        store.Knowledge.ListAsync(cancellationToken);

    public Task<KnowledgeBase?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        store.Knowledge.GetAsync(id, cancellationToken);

    public async Task<KnowledgeBase> CreateAsync(KnowledgeBase knowledgeBase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);

        if (await store.Knowledge.GetAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new NetCoreAIException($"A knowledge base with id '{knowledgeBase.Id}' already exists.");
        }

        await store.Knowledge.UpsertAsync(knowledgeBase, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Created knowledge base {Id} ({Name}) using embedding model {Model}.", knowledgeBase.Id, knowledgeBase.Name, knowledgeBase.EmbeddingModel);
        return knowledgeBase;
    }

    public async Task<KnowledgeBase> UpdateAsync(KnowledgeBase knowledgeBase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);

        var existing = await Require(knowledgeBase.Id, cancellationToken).ConfigureAwait(false);

        // Changing the embedding model invalidates every vector: they are not comparable across models.
        // Saying so is better than silently returning nonsense from a mixed index.
        if (!existing.EmbeddingModel.Equals(knowledgeBase.EmbeddingModel, StringComparison.OrdinalIgnoreCase) && existing.ChunkCount > 0)
        {
            throw new NetCoreAIException(
                $"'{existing.Name}' was indexed with '{existing.EmbeddingModel}'. Vectors from two models cannot be compared, so switching to '{knowledgeBase.EmbeddingModel}' needs the base re-indexed: delete its documents first, or create a new base.");
        }

        await store.Knowledge.UpsertAsync(knowledgeBase, cancellationToken).ConfigureAwait(false);
        return knowledgeBase;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var knowledgeBase = await Require(id, cancellationToken).ConfigureAwait(false);

        // Vectors first: a failure here leaves the base visible and retryable, whereas the reverse would
        // orphan a collection nothing points at.
        try
        {
            await VectorStore(knowledgeBase).DeleteCollectionAsync(knowledgeBase.Collection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not drop the vector collection for {Id}; the metadata was kept so this can be retried.", id);
            throw new NetCoreAIException($"The vectors for '{knowledgeBase.Name}' could not be removed: {ex.Message}", ex);
        }

        await store.Knowledge.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        // Uploaded files belong to the base and would otherwise sit in the data directory for ever.
        var folder = FileDataSource.UploadFolder(options.CurrentValue.DataDirectory, id);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete the upload folder {Folder}; the Storage page will list it as reclaimable.", folder);
        }

        logger.LogInformation("Deleted knowledge base {Id}.", id);
    }

    public Task<IReadOnlyList<DataSourceDefinition>> ListSourcesAsync(string knowledgeBaseId, CancellationToken cancellationToken = default) =>
        store.Knowledge.ListSourcesAsync(knowledgeBaseId, cancellationToken);

    public async Task<DataSourceDefinition> SaveSourceAsync(DataSourceDefinition source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        await Require(source.KnowledgeBaseId, cancellationToken).ConfigureAwait(false);
        Resolve(source.Type);

        await store.Knowledge.UpsertSourceAsync(source, cancellationToken).ConfigureAwait(false);
        return source;
    }

    public async Task DeleteSourceAsync(string sourceId, bool deleteDocuments = true, CancellationToken cancellationToken = default)
    {
        var source = await store.Knowledge.GetSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return;
        }

        if (deleteDocuments && await store.Knowledge.GetAsync(source.KnowledgeBaseId, cancellationToken).ConfigureAwait(false) is { } knowledgeBase)
        {
            foreach (var document in await store.Knowledge.ListDocumentsAsync(source.KnowledgeBaseId, sourceId, cancellationToken).ConfigureAwait(false))
            {
                await RemoveDocumentAsync(knowledgeBase, document, cancellationToken).ConfigureAwait(false);
            }
        }

        await store.Knowledge.DeleteSourceAsync(sourceId, cancellationToken).ConfigureAwait(false);
    }

    public Task<DataSourceTestResult> TestSourceAsync(DataSourceDefinition source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Resolve(source.Type).TestAsync(source, cancellationToken);
    }

    public async Task<JobRecord> SyncAsync(string knowledgeBaseId, string? dataSourceId = null, CancellationToken cancellationToken = default)
    {
        var knowledgeBase = await Require(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
        var sources = await SourcesToSyncAsync(knowledgeBase, dataSourceId, cancellationToken).ConfigureAwait(false);

        return await jobs.EnqueueAsync(
            "kb.sync",
            dataSourceId ?? knowledgeBaseId,
            context => RunSyncAsync(knowledgeBase.Id, sources, context),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks each source's documents, ingesting what has changed. One document that fails is recorded
    /// against the job and the rest carry on: a single corrupt PDF must not abandon a 500-file sync.
    /// </summary>
    private async Task RunSyncAsync(string knowledgeBaseId, List<DataSourceDefinition> sources, IJobContext context)
    {
        var knowledgeBase = await Require(knowledgeBaseId, context.CancellationToken).ConfigureAwait(false);
        var ingested = 0;
        var skipped = 0;

        // Sources are enumerated one at a time, so the total grows as each is read rather than being
        // known up front; setting it per source would make the second source reset the first one's count.
        var total = 0;

        foreach (var definition in sources)
        {
            var source = Resolve(definition.Type);
            await context.AdvanceAsync(0, $"Reading {definition.Name}").ConfigureAwait(false);

            var documents = new List<SourceDocument>();
            try
            {
                await foreach (var document in source.EnumerateAsync(definition, context.CancellationToken).ConfigureAwait(false))
                {
                    documents.Add(document);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The source itself is broken; record it and try the next one rather than failing the job.
                await context.FailItemAsync(definition.Name, ex.Message).ConfigureAwait(false);
                await store.Knowledge.UpsertSourceAsync(definition with { LastError = ex.Message }, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            total += documents.Count;
            await context.SetTotalAsync(total).ConfigureAwait(false);

            foreach (var document in documents)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await pipeline.IngestAsync(knowledgeBase, document, definition.Id, context.CancellationToken).ConfigureAwait(false);
                    if (result.Skipped)
                    {
                        skipped++;
                    }
                    else
                    {
                        ingested++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await context.FailItemAsync(document.Title, ex.Message).ConfigureAwait(false);
                }

                await context.AdvanceAsync(1, $"{definition.Name}: {document.Title}").ConfigureAwait(false);
            }

            await store.Knowledge.UpsertSourceAsync(
                definition with { LastSyncedAt = DateTimeOffset.UtcNow, LastError = null },
                CancellationToken.None).ConfigureAwait(false);
        }

        await RefreshCountsAsync(knowledgeBaseId, CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Sync of {KnowledgeBase} finished: {Ingested} ingested, {Skipped} unchanged.", knowledgeBaseId, ingested, skipped);
    }

    public async Task<KnowledgeDocument> IngestAsync(string knowledgeBaseId, SourceDocument document, string? dataSourceId = null, CancellationToken cancellationToken = default)
    {
        var knowledgeBase = await Require(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
        var result = await pipeline.IngestAsync(knowledgeBase, document, dataSourceId, cancellationToken).ConfigureAwait(false);
        await RefreshCountsAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
        return result.Document;
    }

    public Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string knowledgeBaseId, string? dataSourceId = null, CancellationToken cancellationToken = default) =>
        store.Knowledge.ListDocumentsAsync(knowledgeBaseId, dataSourceId, cancellationToken);

    public async Task DeleteDocumentAsync(string knowledgeBaseId, string documentId, CancellationToken cancellationToken = default)
    {
        var knowledgeBase = await Require(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
        var document = await store.Knowledge.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (document is null || document.KnowledgeBaseId != knowledgeBaseId)
        {
            return;
        }

        await RemoveDocumentAsync(knowledgeBase, document, cancellationToken).ConfigureAwait(false);
        await RefreshCountsAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops a document's chunks before its row, so nothing stays retrievable after the row is gone.</summary>
    private async Task RemoveDocumentAsync(KnowledgeBase knowledgeBase, KnowledgeDocument document, CancellationToken cancellationToken)
    {
        await VectorStore(knowledgeBase).DeleteByDocumentAsync(knowledgeBase.Collection, document.Id, cancellationToken).ConfigureAwait(false);
        await store.Knowledge.DeleteDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recomputes the counters the UI shows, from the rows rather than by incrementing.</summary>
    private async Task RefreshCountsAsync(string knowledgeBaseId, CancellationToken cancellationToken)
    {
        if (await store.Knowledge.GetAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false) is not { } knowledgeBase)
        {
            return;
        }

        var documents = await store.Knowledge.ListDocumentsAsync(knowledgeBaseId, cancellationToken: cancellationToken).ConfigureAwait(false);
        await store.Knowledge.UpsertAsync(
            knowledgeBase with
            {
                DocumentCount = documents.Count,
                ChunkCount = documents.Sum(d => d.ChunkCount),
                LastIngestedAt = documents.Count == 0 ? knowledgeBase.LastIngestedAt : DateTimeOffset.UtcNow,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The sources a sync should walk: one named source, or every enabled one plus host pushes.</summary>
    private async Task<List<DataSourceDefinition>> SourcesToSyncAsync(KnowledgeBase knowledgeBase, string? dataSourceId, CancellationToken cancellationToken)
    {
        var all = await store.Knowledge.ListSourcesAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false);
        if (dataSourceId is { Length: > 0 })
        {
            var one = all.FirstOrDefault(s => s.Id == dataSourceId)
                ?? throw new NetCoreAIException($"No data source '{dataSourceId}' is attached to '{knowledgeBase.Name}'.");

            return [one];
        }

        var enabled = all.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0 && !_hostSources.Any(h => h.KnowledgeBaseId == knowledgeBase.Id))
        {
            throw new NetCoreAIException(
                $"'{knowledgeBase.Name}' has no enabled data source to sync. Add one, or push documents with IKnowledgeClient.IngestAsync.");
        }

        return enabled;
    }

    private IDataSource Resolve(string type) =>
        _dataSources.FirstOrDefault(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        ?? throw new NetCoreAIException(
            $"No data source of type '{type}' is registered. Available types: {(_dataSources.Count == 0 ? "none" : string.Join(", ", SourceTypes))}.");

    private IVectorStore VectorStore(KnowledgeBase knowledgeBase) =>
        _vectorStores.FirstOrDefault(v => v.Id.Equals(knowledgeBase.VectorStoreId, StringComparison.OrdinalIgnoreCase))
        ?? _vectorStores.FirstOrDefault()
        ?? throw new NetCoreAIException($"No vector store with id '{knowledgeBase.VectorStoreId}' is registered.");

    private async Task<KnowledgeBase> Require(string id, CancellationToken cancellationToken) =>
        await store.Knowledge.GetAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new NetCoreAIException($"No knowledge base with id '{id}' exists.");
}
