using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Knowledge;

/// <summary>What one ingestion produced.</summary>
/// <param name="Document">The stored document row.</param>
/// <param name="ChunkCount">Chunks written to the vector store.</param>
/// <param name="Skipped">True when the content was unchanged and nothing was re-embedded.</param>
public sealed record IngestionResult(KnowledgeDocument Document, int ChunkCount, bool Skipped);

/// <summary>
/// Extract → chunk → embed → store, for one document at a time.
/// </summary>
/// <remarks>
/// Every stage is replaceable through DI: extractors per format, chunkers per strategy, and the embedding
/// model named by the knowledge base. Content is hashed after extraction, so re-ingesting an unchanged
/// document costs one extraction rather than an embedding run over every chunk.
/// </remarks>
public interface IIngestionPipeline
{
    Task<IngestionResult> IngestAsync(KnowledgeBase knowledgeBase, SourceDocument document, string? dataSourceId = null, CancellationToken cancellationToken = default);
}

internal sealed class IngestionPipeline(
    IEnumerable<IDocumentExtractor> extractors,
    IEnumerable<IChunker> chunkers,
    IEnumerable<IVectorStore> vectorStores,
    IChatClientFactory clients,
    IMetadataStore store,
    ILogger<IngestionPipeline> logger) : IIngestionPipeline
{
    /// <summary>Chunks embedded per call. Big enough to amortise the round trip, small enough to bound memory.</summary>
    private const int EmbeddingBatchSize = 32;

    private readonly List<IDocumentExtractor> _extractors = [.. extractors.OrderByDescending(e => e.Priority)];
    private readonly List<IChunker> _chunkers = [.. chunkers];
    private readonly List<IVectorStore> _vectorStores = [.. vectorStores];

    public async Task<IngestionResult> IngestAsync(
        KnowledgeBase knowledgeBase,
        SourceDocument document,
        string? dataSourceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentNullException.ThrowIfNull(document);

        // 1. Extract.
        var extracted = await ExtractAsync(document, cancellationToken).ConfigureAwait(false);
        var hash = HashOf(extracted);

        // 2. Skip unchanged content. The id is stable per source document, so this is an update check
        //    rather than a duplicate check: same id and same hash means there is nothing to do.
        var documentId = DocumentId(knowledgeBase.Id, dataSourceId, document.Id);
        var existing = await store.Knowledge.GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (existing is { ContentHash: { } previous } && previous == hash)
        {
            logger.LogDebug("{Title} is unchanged since it was last ingested; skipping.", document.Title);
            return new IngestionResult(existing, existing.ChunkCount, Skipped: true);
        }

        // 3. Chunk.
        var chunker = _chunkers.FirstOrDefault(c => c.Strategy == knowledgeBase.Chunking.Strategy)
            ?? _chunkers.FirstOrDefault()
            ?? throw new NetCoreAIException("No chunker is registered.");

        var chunks = chunker.Chunk(extracted, knowledgeBase.Chunking);
        if (chunks.Count == 0)
        {
            throw new NetCoreAIException(
                $"'{document.Title}' produced no text worth indexing. It may be an image-only PDF, which needs OCR, or an empty file.");
        }

        // 4. Embed and store.
        var vectorStore = VectorStore(knowledgeBase);
        var records = await EmbedAsync(knowledgeBase, document, documentId, extracted, chunks, cancellationToken).ConfigureAwait(false);

        await vectorStore.EnsureCollectionAsync(knowledgeBase.Collection, records[0].Embedding.Length, VectorDistance.Cosine, cancellationToken).ConfigureAwait(false);

        // Replacing a document means dropping what it had before: an edit that removes a paragraph must
        // not leave that paragraph retrievable.
        await vectorStore.DeleteByDocumentAsync(knowledgeBase.Collection, documentId, cancellationToken).ConfigureAwait(false);
        await vectorStore.UpsertAsync(knowledgeBase.Collection, records, cancellationToken).ConfigureAwait(false);

        var stored = new KnowledgeDocument
        {
            Id = documentId,
            KnowledgeBaseId = knowledgeBase.Id,
            DataSourceId = dataSourceId,
            Title = document.Title,
            Source = document.Source ?? document.FileName,
            ContentType = document.ContentType,
            SizeBytes = document.SizeBytes,
            ContentHash = hash,
            ChunkCount = records.Count,
            Metadata = Merge(extracted.Metadata, document.Metadata),
            AclTags = Acl(knowledgeBase, document),
            SourceModifiedAt = document.ModifiedAt,
        };

        await store.Knowledge.UpsertDocumentAsync(stored, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Ingested {Title} into {KnowledgeBase}: {Chunks} chunk(s).", document.Title, knowledgeBase.Name, records.Count);
        return new IngestionResult(stored, records.Count, Skipped: false);
    }

    private async Task<ExtractedDocument> ExtractAsync(SourceDocument document, CancellationToken cancellationToken)
    {
        var fileName = document.FileName ?? document.Title;
        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(fileName, document.ContentType))
            ?? throw new NetCoreAIException(
                $"No extractor handles '{fileName}'. Supported formats come from the registered IDocumentExtractor implementations; add a package or convert the file.");

        await using var content = await document.OpenAsync(cancellationToken).ConfigureAwait(false);
        var extracted = await extractor.ExtractAsync(content, fileName, document.ContentType, cancellationToken).ConfigureAwait(false);

        return extracted.Sections.Count == 0
            ? throw new NetCoreAIException($"'{document.Title}' contained no extractable text.")
            : extracted;
    }

    /// <summary>Embeds chunks in batches, carrying each chunk's provenance onto its record.</summary>
    private async Task<List<VectorRecord>> EmbedAsync(
        KnowledgeBase knowledgeBase,
        SourceDocument document,
        string documentId,
        ExtractedDocument extracted,
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken)
    {
        var generator = clients.GetEmbeddingGenerator(knowledgeBase.EmbeddingModel);
        var acl = Acl(knowledgeBase, document);
        var baseMetadata = Merge(extracted.Metadata, document.Metadata);
        var records = new List<VectorRecord>(chunks.Count);

        for (var offset = 0; offset < chunks.Count; offset += EmbeddingBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = chunks.Skip(offset).Take(EmbeddingBatchSize).ToList();

            GeneratedEmbeddings<Embedding<float>> embeddings;
            try
            {
                embeddings = await generator.GenerateAsync([.. batch.Select(c => c.Text)], cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not NetCoreAIException)
            {
                throw new NetCoreAIException(
                    $"The embedding model '{knowledgeBase.EmbeddingModel}' failed while indexing '{document.Title}': {ex.Message}", ex);
            }

            if (embeddings.Count != batch.Count)
            {
                throw new NetCoreAIException(
                    $"The embedding model returned {embeddings.Count} vectors for {batch.Count} chunks, so the index would not line up with the text.");
            }

            for (var i = 0; i < batch.Count; i++)
            {
                var chunk = batch[i];
                records.Add(new VectorRecord(
                    $"{documentId}:{chunk.Index}",
                    documentId,
                    embeddings[i].Vector,
                    chunk.Text,
                    ChunkMetadata(baseMetadata, document, chunk),
                    acl));
            }
        }

        return records;
    }

    /// <summary>Metadata stored per chunk: what retrieval filters on, and what a citation is built from.</summary>
    private static Dictionary<string, string> ChunkMetadata(IReadOnlyDictionary<string, string> baseMetadata, SourceDocument document, DocumentChunk chunk)
    {
        var metadata = new Dictionary<string, string>(baseMetadata, StringComparer.Ordinal)
        {
            ["title"] = document.Title,
            ["chunk_index"] = chunk.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (chunk.Page is { } page)
        {
            metadata["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (chunk.Section is { Length: > 0 } section)
        {
            metadata["section"] = section;
        }

        if (document.Source is { Length: > 0 } source)
        {
            metadata["source"] = source;
        }

        if (chunk.Metadata is not null)
        {
            foreach (var (key, value) in chunk.Metadata)
            {
                metadata[key] = value;
            }
        }

        return metadata;
    }

    private IVectorStore VectorStore(KnowledgeBase knowledgeBase) =>
        _vectorStores.FirstOrDefault(v => v.Id.Equals(knowledgeBase.VectorStoreId, StringComparison.OrdinalIgnoreCase))
        ?? _vectorStores.FirstOrDefault()
        ?? throw new NetCoreAIException(
            $"No vector store with id '{knowledgeBase.VectorStoreId}' is registered. Reference the NetCoreAI meta-package for the SQLite store, or register another.");

    /// <summary>The base's default tags plus anything the document carries; empty stays public.</summary>
    private static IReadOnlyList<string> Acl(KnowledgeBase knowledgeBase, SourceDocument document)
    {
        if (document.AclTags is not { Count: > 0 } && knowledgeBase.DefaultAclTags.Count == 0)
        {
            return [];
        }

        return [.. knowledgeBase.DefaultAclTags.Concat(document.AclTags ?? []).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string>? second)
    {
        var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
        if (second is not null)
        {
            foreach (var (key, value) in second)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>Stable per source document, so re-ingesting updates in place rather than duplicating.</summary>
    internal static string DocumentId(string knowledgeBaseId, string? dataSourceId, string sourceDocumentId)
    {
        var key = $"{knowledgeBaseId}|{dataSourceId}|{sourceDocumentId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
    }

    /// <summary>Hash of the extracted text: what decides whether a document has actually changed.</summary>
    internal static string HashOf(ExtractedDocument document)
    {
        using var sha = SHA256.Create();
        foreach (var section in document.Sections)
        {
            var bytes = Encoding.UTF8.GetBytes(section.Text);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
