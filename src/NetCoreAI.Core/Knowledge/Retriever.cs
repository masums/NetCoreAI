using Microsoft.Extensions.Logging;

namespace NetCoreAI.Knowledge;

/// <summary>
/// Finds the passages that answer a query: embed it with the base's own model, take the nearest chunks,
/// and filter them by metadata and by what the caller is allowed to see.
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Searches one knowledge base.
    /// </summary>
    /// <param name="knowledgeBaseId">Base to search.</param>
    /// <param name="query">The user's question, embedded with the base's embedding model.</param>
    /// <param name="options">Retrieval settings; the base's own defaults are used when null.</param>
    /// <param name="callerTags">
    /// Access tags the caller holds. Null means no filtering, which is for system callers only: passing
    /// null on behalf of a user would return passages that user cannot see.
    /// </param>
    /// <param name="cancellationToken">Cancels the search.</param>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string knowledgeBaseId,
        string query,
        RetrievalOptions? options = null,
        IReadOnlyList<string>? callerTags = null,
        CancellationToken cancellationToken = default);

    /// <summary>Searches several bases and merges the results by score, so an answer can draw on all of them.</summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchManyAsync(
        IReadOnlyList<string> knowledgeBaseIds,
        string query,
        RetrievalOptions? options = null,
        IReadOnlyList<string>? callerTags = null,
        CancellationToken cancellationToken = default);
}

internal sealed class Retriever(
    IMetadataStore store,
    IChatClientFactory clients,
    IEnumerable<IVectorStore> vectorStores,
    ILogger<Retriever> logger) : IRetriever
{
    private readonly List<IVectorStore> _vectorStores = [.. vectorStores];

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string knowledgeBaseId,
        string query,
        RetrievalOptions? options = null,
        IReadOnlyList<string>? callerTags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseId);

        var knowledgeBase = await store.Knowledge.GetAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new NetCoreAIException($"No knowledge base with id '{knowledgeBaseId}' exists.");

        var results = await SearchCoreAsync(knowledgeBase, query, options, callerTags, cancellationToken).ConfigureAwait(false);
        return Number(results);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchManyAsync(
        IReadOnlyList<string> knowledgeBaseIds,
        string query,
        RetrievalOptions? options = null,
        IReadOnlyList<string>? callerTags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBaseIds);

        if (knowledgeBaseIds.Count == 1)
        {
            return await SearchAsync(knowledgeBaseIds[0], query, options, callerTags, cancellationToken).ConfigureAwait(false);
        }

        var merged = new List<RetrievedChunk>();
        foreach (var id in knowledgeBaseIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var knowledgeBase = await store.Knowledge.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (knowledgeBase is null)
            {
                // One missing base should not fail a multi-base search; the others still have answers.
                logger.LogWarning("Knowledge base {Id} was requested but does not exist; skipping it.", id);
                continue;
            }

            merged.AddRange(await SearchCoreAsync(knowledgeBase, query, options, callerTags, cancellationToken).ConfigureAwait(false));
        }

        // Scores are cosine similarities from comparable models, so a straight sort is meaningful.
        var topK = options?.TopK ?? RetrievalOptions.Default.TopK;
        return Number([.. merged.OrderByDescending(r => r.Score).Take(topK)]);
    }

    private async Task<List<RetrievedChunk>> SearchCoreAsync(
        KnowledgeBase knowledgeBase,
        string query,
        RetrievalOptions? options,
        IReadOnlyList<string>? callerTags,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var settings = options ?? knowledgeBase.Retrieval;
        var vectorStore = VectorStore(knowledgeBase);

        // An empty base has no collection yet; searching it is not an error, it just has no answers.
        if (!(await vectorStore.ListCollectionsAsync(cancellationToken).ConfigureAwait(false)).Contains(knowledgeBase.Collection, StringComparer.Ordinal))
        {
            logger.LogDebug("{KnowledgeBase} has nothing indexed yet.", knowledgeBase.Name);
            return [];
        }

        var embedding = await EmbedAsync(knowledgeBase, query, cancellationToken).ConfigureAwait(false);
        var filter = new VectorFilter
        {
            MetadataEquals = settings.MetadataFilter,
            DocumentIds = settings.DocumentIds,
            CallerTags = callerTags,
            MinScore = settings.MinScore,
        };

        var hits = await vectorStore.SearchAsync(knowledgeBase.Collection, embedding, Math.Max(1, settings.TopK), filter, cancellationToken).ConfigureAwait(false);
        var results = new List<RetrievedChunk>(hits.Count);

        foreach (var hit in hits)
        {
            results.Add(new RetrievedChunk(
                settings.IncludeText ? hit.Record : hit.Record with { Text = string.Empty },
                hit.Score,
                ToCitation(knowledgeBase, hit)));
        }

        logger.LogDebug("Retrieved {Count} chunk(s) from {KnowledgeBase} for a {Length}-character query.", results.Count, knowledgeBase.Name, query.Length);
        return results;
    }

    /// <summary>
    /// Embeds the query with the same model the documents were embedded with. Using a different one would
    /// return confident nonsense, so the failure says which model is at fault.
    /// </summary>
    private async Task<ReadOnlyMemory<float>> EmbedAsync(KnowledgeBase knowledgeBase, string query, CancellationToken cancellationToken)
    {
        try
        {
            var generator = clients.GetEmbeddingGenerator(knowledgeBase.EmbeddingModel);
            var embeddings = await generator.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);
            return embeddings[0].Vector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NetCoreAIException)
        {
            throw new NetCoreAIException(
                $"The query could not be embedded with '{knowledgeBase.EmbeddingModel}', the model '{knowledgeBase.Name}' was indexed with: {ex.Message}",
                ex);
        }
    }

    /// <summary>Builds the citation shown under an answer, from the metadata the pipeline stored per chunk.</summary>
    private static Citation ToCitation(KnowledgeBase knowledgeBase, VectorSearchResult hit)
    {
        var metadata = hit.Record.Metadata;
        return new Citation(
            hit.Record.DocumentId,
            metadata.GetValueOrDefault("title") ?? "(untitled)",
            Snippet(hit.Record.Text),
            hit.Score)
        {
            Page = metadata.TryGetValue("page", out var page) && int.TryParse(page, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null,
            Section = metadata.GetValueOrDefault("section"),
            Source = metadata.GetValueOrDefault("source"),
            KnowledgeBaseId = knowledgeBase.Id,
        };
    }

    /// <summary>A short quote for display; the full chunk stays on the record for anyone who wants it.</summary>
    private static string Snippet(string text)
    {
        const int limit = 300;
        if (text.Length <= limit)
        {
            return text;
        }

        // Cut on a word boundary so the quote does not end mid-word.
        var cut = text.LastIndexOf(' ', limit);
        return string.Concat(text.AsSpan(0, cut > limit / 2 ? cut : limit).Trim(), "…");
    }

    /// <summary>Numbers the citations so "[2]" in an answer lines up with the second source shown.</summary>
    private static List<RetrievedChunk> Number(List<RetrievedChunk> results)
    {
        for (var i = 0; i < results.Count; i++)
        {
            results[i] = results[i] with { Citation = results[i].Citation with { Ordinal = i + 1 } };
        }

        return results;
    }

    private IVectorStore VectorStore(KnowledgeBase knowledgeBase) =>
        _vectorStores.FirstOrDefault(v => v.Id.Equals(knowledgeBase.VectorStoreId, StringComparison.OrdinalIgnoreCase))
        ?? _vectorStores.FirstOrDefault()
        ?? throw new NetCoreAIException($"No vector store with id '{knowledgeBase.VectorStoreId}' is registered.");
}
