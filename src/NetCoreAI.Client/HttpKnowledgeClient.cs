using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetCoreAI.Client;

/// <summary>
/// <see cref="IKnowledgeClient"/> against a remote NetCoreAI host's <c>/api/kb</c> endpoints.
/// </summary>
/// <remarks>
/// The same interface the in-process client implements, so code written against <see cref="IKnowledgeClient"/>
/// moves between "inside the host" and "a separate application" without changing. Two differences are
/// inherent to the wire rather than accidental, and are called out on the members that carry them:
/// retrieved chunks arrive without their embeddings, and the caller's access tags come from the API key or
/// signed-in identity on the server rather than from an argument here.
/// </remarks>
internal sealed class HttpKnowledgeClient(IHttpClientFactory factory) : IKnowledgeClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private HttpClient Http => factory.CreateClient(NetCoreAIClientServiceCollectionExtensions.HttpClientName);

    public async Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, "api/kb", content: null, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<ListResponse>(response, cancellationToken).ConfigureAwait(false);
        return payload?.KnowledgeBases ?? [];
    }

    public async Task<KnowledgeBase?> GetAsync(string knowledgeBaseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseId);

        var response = await SendAsync(HttpMethod.Get, $"api/kb/{Uri.EscapeDataString(knowledgeBaseId)}", content: null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // "No such base" is an answer, not a failure: the in-process client returns null here too.
            return null;
        }

        var payload = await ReadAsync<GetResponse>(response, cancellationToken).ConfigureAwait(false);
        return payload?.KnowledgeBase;
    }

    /// <summary>
    /// Uploads the document's content and ingests it.
    /// </summary>
    /// <remarks>
    /// The stream is sent as the request body rather than being read into memory first, so pushing a large
    /// PDF from a client application costs the same as pushing a small one.
    /// </remarks>
    public async Task<KnowledgeDocument> IngestAsync(string knowledgeBaseId, SourceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseId);
        ArgumentNullException.ThrowIfNull(document);

        var fileName = document.FileName is { Length: > 0 } name ? name : $"{document.Id}.txt";
        await using var stream = await document.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var body = new StreamContent(stream);
        if (document.ContentType is { Length: > 0 } contentType)
        {
            body.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        var url = $"api/kb/{Uri.EscapeDataString(knowledgeBaseId)}/documents/upload?fileName={Uri.EscapeDataString(fileName)}";
        var response = await SendAsync(HttpMethod.Post, url, body, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<KnowledgeDocument>(response, cancellationToken).ConfigureAwait(false)
            ?? throw new NetCoreAIException("The host accepted the document but returned nothing describing it.");
    }

    public async Task<string> SyncAsync(string knowledgeBaseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseId);

        var response = await SendAsync(HttpMethod.Post, $"api/kb/{Uri.EscapeDataString(knowledgeBaseId)}/ingest", content: null, cancellationToken).ConfigureAwait(false);
        var job = await ReadAsync<JobResponse>(response, cancellationToken).ConfigureAwait(false);
        return job?.Id ?? throw new NetCoreAIException("The host queued the sync but returned no job id to watch.");
    }

    /// <summary>
    /// Searches one knowledge base.
    /// </summary>
    /// <remarks>
    /// <paramref name="callerTags"/> is ignored: a client cannot be trusted to declare its own access tags,
    /// so the server derives them from the API key or the signed-in user. Passing tags here would look like
    /// it worked while silently doing nothing, so it throws instead.
    /// Returned chunks carry no embedding — the server does not send vectors — but do carry their id,
    /// document id, text, metadata and tags.
    /// </remarks>
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string knowledgeBaseId,
        string query,
        RetrievalOptions? options = null,
        IReadOnlyList<string>? callerTags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (callerTags is not null)
        {
            throw new NetCoreAIException(
                "Access tags cannot be set from a client: the host decides what a caller may see, from its API key or signed-in identity. Leave callerTags null.");
        }

        using var body = JsonContent.Create(new { query, options }, options: Json);
        var response = await SendAsync(HttpMethod.Post, $"api/kb/{Uri.EscapeDataString(knowledgeBaseId)}/search", body, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<SearchResponse>(response, cancellationToken).ConfigureAwait(false);

        return [.. (payload?.Results ?? []).Select(r => new RetrievedChunk(
            new VectorRecord(
                r.ChunkId ?? string.Empty,
                r.DocumentId ?? r.Citation?.DocumentId ?? string.Empty,
                ReadOnlyMemory<float>.Empty,
                r.Text ?? string.Empty,
                r.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
                r.AclTags ?? []),
            r.Score,
            r.Citation ?? new Citation(r.DocumentId ?? string.Empty, string.Empty, r.Text ?? string.Empty, r.Score)))];
    }

    public async Task DeleteDocumentAsync(string knowledgeBaseId, string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        await SendAsync(
            HttpMethod.Delete,
            $"api/kb/{Uri.EscapeDataString(knowledgeBaseId)}/documents/{Uri.EscapeDataString(documentId)}",
            content: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // "Connection refused" on its own does not say which host was unreachable, and a client app
            // pointed at the wrong base URL is the commonest cause of seeing it.
            throw new NetCoreAIException($"Could not reach the NetCoreAI host at {Http.BaseAddress}: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return response;
        }

        // The host answers failures as ProblemDetails, so the message a user sees here is the one the
        // server wrote rather than a bare status code.
        var detail = await ProblemAsync(response, cancellationToken).ConfigureAwait(false);
        response.Dispose();
        throw new NetCoreAIException($"The NetCoreAI host refused the request ({(int)response.StatusCode}): {detail}");
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body is { Length: > 0 } && JsonSerializer.Deserialize<ProblemBody>(body, Json) is { } problem)
            {
                return problem.Detail ?? problem.Error ?? problem.Title ?? body;
            }

            return body is { Length: > 0 } ? body : response.ReasonPhrase ?? "no detail given";
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "no detail given";
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            {
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ListResponse(IReadOnlyList<KnowledgeBase>? KnowledgeBases);

    private sealed record GetResponse(KnowledgeBase? KnowledgeBase);

    private sealed record JobResponse(string? Id);

    private sealed record SearchResponse(IReadOnlyList<SearchHit>? Results);

    private sealed record SearchHit(float Score)
    {
        public Citation? Citation { get; init; }

        public string? ChunkId { get; init; }

        public string? DocumentId { get; init; }

        public string? Text { get; init; }

        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        public IReadOnlyList<string>? AclTags { get; init; }
    }

    private sealed record ProblemBody
    {
        public string? Title { get; init; }

        public string? Detail { get; init; }

        /// <summary>Endpoints that answer <c>{ "error": "..." }</c> rather than ProblemDetails.</summary>
        public string? Error { get; init; }
    }
}
