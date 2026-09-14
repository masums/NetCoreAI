namespace NetCoreAI;

/// <summary>Convenience helpers over <see cref="IKnowledgeClient"/> for the common shapes of ingest.</summary>
public static class KnowledgeClientExtensions
{
    /// <summary>Ingests a string as a document, which is what host code pushing its own records usually wants.</summary>
    public static Task<KnowledgeDocument> IngestTextAsync(
        this IKnowledgeClient client,
        string knowledgeBaseId,
        string id,
        string title,
        string text,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyList<string>? aclTags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
        return client.IngestAsync(knowledgeBaseId, new SourceDocument(id, title)
        {
            // Presented as text so the plain-text extractor handles it; the caller has already decided
            // what the document says.
            FileName = $"{id}.txt",
            ContentType = "text/plain",
            SizeBytes = bytes.Length,
            Metadata = metadata,
            AclTags = aclTags,
            OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes)),
        }, cancellationToken);
    }

    /// <summary>Ingests a file from disk, letting the extractors work out what it is.</summary>
    public static Task<KnowledgeDocument> IngestFileAsync(
        this IKnowledgeClient client,
        string knowledgeBaseId,
        string path,
        string? title = null,
        IReadOnlyList<string>? aclTags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new NetCoreAIException($"No file exists at {path}. The path is read by the server, not by a browser.");
        }

        var info = new FileInfo(path);
        return client.IngestAsync(knowledgeBaseId, new SourceDocument(path, title ?? Path.GetFileNameWithoutExtension(path))
        {
            FileName = info.Name,
            SizeBytes = info.Length,
            Source = info.FullName,
            ModifiedAt = info.LastWriteTimeUtc,
            AclTags = aclTags,
            OpenAsync = _ => Task.FromResult<Stream>(new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true)),
        }, cancellationToken);
    }
}
