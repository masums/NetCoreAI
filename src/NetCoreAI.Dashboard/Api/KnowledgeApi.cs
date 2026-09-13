using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Knowledge;

namespace NetCoreAI.Dashboard.Api;

/// <summary>
/// Knowledge bases, their data sources, their documents and search.
/// </summary>
internal static class KnowledgeApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var kb = api.MapGroup("/kb");

        kb.MapGet("/", async (IKnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(new
            {
                knowledgeBases = await knowledge.ListAsync(ct),
                sourceTypes = knowledge.SourceTypes,
            })).WithName("NetCoreAI.Kb.List");

        kb.MapGet("/{id}", async (string id, IKnowledgeService knowledge, CancellationToken ct) =>
        {
            var knowledgeBase = await knowledge.GetAsync(id, ct);
            return knowledgeBase is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    knowledgeBase,
                    sources = await knowledge.ListSourcesAsync(id, ct),
                    documents = await knowledge.ListDocumentsAsync(id, cancellationToken: ct),
                });
        }).WithName("NetCoreAI.Kb.Get");

        kb.MapPost("/", async (KnowledgeBase knowledgeBase, IKnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(await knowledge.CreateAsync(knowledgeBase, ct))).WithName("NetCoreAI.Kb.Create");

        kb.MapPut("/{id}", async (string id, KnowledgeBase knowledgeBase, IKnowledgeService knowledge, CancellationToken ct) =>
            id != knowledgeBase.Id
                ? Results.BadRequest(new { error = "The id in the URL and the body must match." })
                : Results.Ok(await knowledge.UpdateAsync(knowledgeBase, ct))).WithName("NetCoreAI.Kb.Update");

        kb.MapDelete("/{id}", async (string id, IKnowledgeService knowledge, CancellationToken ct) =>
        {
            await knowledge.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Kb.Delete");

        MapSources(kb);
        MapDocuments(kb);
        MapSearch(kb);
    }

    private static void MapSources(RouteGroupBuilder kb)
    {
        kb.MapGet("/{id}/sources", async (string id, IKnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(await knowledge.ListSourcesAsync(id, ct))).WithName("NetCoreAI.Kb.Sources");

        kb.MapPost("/{id}/sources", async (string id, DataSourceDefinition source, IKnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(await knowledge.SaveSourceAsync(source with { KnowledgeBaseId = id }, ct))).WithName("NetCoreAI.Kb.SaveSource");

        kb.MapPost("/{id}/sources/test", async (string id, DataSourceDefinition source, IKnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(await knowledge.TestSourceAsync(source with { KnowledgeBaseId = id }, ct))).WithName("NetCoreAI.Kb.TestSource");

        kb.MapDelete("/{id}/sources/{sourceId}", async (string sourceId, IKnowledgeService knowledge, bool deleteDocuments = true, CancellationToken ct = default) =>
        {
            await knowledge.DeleteSourceAsync(sourceId, deleteDocuments, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Kb.DeleteSource");

        kb.MapPost("/{id}/ingest", async (string id, IKnowledgeService knowledge, string? sourceId = null, CancellationToken ct = default) =>
        {
            // Queued rather than awaited: a folder of 500 PDFs must not hold a request open.
            var job = await knowledge.SyncAsync(id, sourceId, ct);
            return Results.Accepted($"jobs/{job.Id}", job);
        }).WithName("NetCoreAI.Kb.Ingest");

        kb.MapGet("/{id}/jobs", async (string id, IBackgroundJobRunner jobs, CancellationToken ct) =>
            Results.Ok(await jobs.ListAsync(id, ct))).WithName("NetCoreAI.Kb.Jobs");
    }

    private static void MapDocuments(RouteGroupBuilder kb)
    {
        kb.MapGet("/{id}/documents", async (string id, IKnowledgeService knowledge, string? sourceId = null, CancellationToken ct = default) =>
            Results.Ok(await knowledge.ListDocumentsAsync(id, sourceId, ct))).WithName("NetCoreAI.Kb.Documents");

        kb.MapPost("/{id}/documents", async (string id, IngestTextRequest request, IKnowledgeClient client, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest(new { error = "Give some text to ingest." });
            }

            var document = await client.IngestTextAsync(
                id,
                request.Id ?? Guid.NewGuid().ToString("N")[..12],
                request.Title ?? "Untitled",
                request.Text,
                request.Metadata,
                request.AclTags,
                ct);

            return Results.Ok(document);
        }).WithName("NetCoreAI.Kb.IngestText");

        kb.MapDelete("/{id}/documents/{documentId}", async (string id, string documentId, IKnowledgeService knowledge, CancellationToken ct) =>
        {
            await knowledge.DeleteDocumentAsync(id, documentId, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Kb.DeleteDocument");
    }

    private static void MapSearch(RouteGroupBuilder kb)
    {
        kb.MapPost("/{id}/search", async (string id, SearchRequest request, IRetriever retriever, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.BadRequest(new { error = "Give a query to search for." });
            }

            var hits = await retriever.SearchAsync(id, request.Query, request.Options, CallerTags(http.User), ct);
            return Results.Ok(new
            {
                results = hits.Select(h => new
                {
                    h.Score,
                    h.Citation,
                    Text = h.Chunk.Text,
                    h.Chunk.Metadata,
                }),
            });
        }).WithName("NetCoreAI.Kb.Search");
    }

    /// <summary>
    /// The access tags a caller holds, built from their claims.
    /// </summary>
    /// <remarks>
    /// An unauthenticated caller gets an empty list rather than null, so they see only public documents.
    /// Null would mean "no filtering at all", which is for system callers: defaulting to it here would
    /// hand every restricted passage to anyone who could reach the endpoint.
    /// </remarks>
    internal static IReadOnlyList<string> CallerTags(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return [];
        }

        var tags = new List<string>();
        foreach (var claim in user.Claims)
        {
            // Claim types are URIs in some identity stacks; the short name is what an author would type.
            var type = claim.Type.Contains('/', StringComparison.Ordinal)
                ? claim.Type[(claim.Type.LastIndexOf('/') + 1)..]
                : claim.Type;

            if (claim.Value is { Length: > 0 })
            {
                tags.Add(AclTag.From(type, claim.Value));
            }
        }

        return tags;
    }

    /// <summary>Text pushed straight into a base, for hosts that already have the content.</summary>
    public sealed record IngestTextRequest(string Text)
    {
        public string? Id { get; init; }

        public string? Title { get; init; }

        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        public IReadOnlyList<string>? AclTags { get; init; }
    }

    /// <summary>A search against one knowledge base.</summary>
    public sealed record SearchRequest(string Query)
    {
        public RetrievalOptions? Options { get; init; }
    }
}
