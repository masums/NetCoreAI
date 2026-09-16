using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Tools;

namespace NetCoreAI.Dashboard.Api;

/// <summary>Tools: what the host could expose to a model, and what it has.</summary>
internal static class ToolsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var tools = api.MapGroup("/tools");

        tools.MapGet("/", async (IToolService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct))).WithName("NetCoreAI.Tools.List");

        tools.MapGet("/discover", async (IEndpointDiscovery discovery, IToolService service, CancellationToken ct) =>
        {
            var endpoints = discovery.Discover();
            var existing = await service.ListAsync(ct);

            // Each row says whether a tool already exists for it, so the designer offers "edit" rather than
            // a second "create" that would fail on the duplicate name.
            // Grouped, not keyed: one endpoint may legitimately have several tools built from it.
            var byEndpoint = existing
                .Where(t => t.Kind == ToolKind.Endpoint && t.Route is not null)
                .GroupBy(t => DiscoveredEndpoint.IdFor(t.Method, t.Route!), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

            return Results.Ok(new
            {
                endpoints = endpoints.Select(e => e with { ExistingToolId = byEndpoint.GetValueOrDefault(e.Id) }),
                optedIn = endpoints.Count(e => e.OptedIn),
            });
        }).WithName("NetCoreAI.Tools.Discover");

        tools.MapGet("/{id}", async (string id, IToolService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } tool ? Results.Ok(tool) : Results.NotFound()).WithName("NetCoreAI.Tools.Get");

        tools.MapPost("/", async (ToolDefinition tool, IToolService service, HttpContext http, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(tool, AllowInProcessBy(tool, http), ct))).WithName("NetCoreAI.Tools.Create");

        tools.MapPut("/{id}", async (string id, ToolDefinition tool, IToolService service, HttpContext http, CancellationToken ct) =>
            id != tool.Id
                ? Results.BadRequest(new { error = "The id in the URL and the body must match." })
                : Results.Ok(await service.SaveAsync(tool, AllowInProcessBy(tool, http), ct))).WithName("NetCoreAI.Tools.Update");

        // Groups: a capability an agent can be given instead of a list of ids.
        tools.MapGet("/groups", async (IToolService service, CancellationToken ct) =>
            Results.Ok(await service.ListGroupsAsync(ct))).WithName("NetCoreAI.Tools.Groups");

        tools.MapPut("/groups/{id}", async (string id, ToolGroup group, IToolService service, CancellationToken ct) =>
            Results.Ok(await service.SaveGroupAsync(group with { Id = id }, ct))).WithName("NetCoreAI.Tools.SaveGroup");

        tools.MapDelete("/groups/{id}", async (string id, IToolService service, CancellationToken ct) =>
        {
            await service.DeleteGroupAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Tools.DeleteGroup");

        // Asked before changing a tool, because the answer is who else it changes.
        tools.MapGet("/{id}/used-by", async (string id, IToolService service, CancellationToken ct) =>
            Results.Ok(await service.UsedByAsync(id, ct))).WithName("NetCoreAI.Tools.UsedBy");

        tools.MapDelete("/{id}", async (string id, IToolService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Tools.Delete");

        tools.MapPost("/from-endpoint/{endpointId}", async (string endpointId, IToolService service, CancellationToken ct) =>
            Results.Ok(await service.CreateFromEndpointAsync(endpointId, ct))).WithName("NetCoreAI.Tools.FromEndpoint");

        tools.MapPost("/{id}/test", async (string id, TestRequest request, IToolTester tester, HttpContext http, CancellationToken ct) =>
        {
            // The trial call runs as whoever is at the dashboard, not as the host: a tool that a caller
            // could not use must not appear to work when it is tried.
            var context = new ToolCallContext
            {
                User = http.User,
                BaseAddress = new Uri($"{http.Request.Scheme}://{http.Request.Host}"),
                AuthorizationHeader = http.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null,
            };

            return Results.Ok(await tester.TestAsync(id, request.Arguments, request.Prompt, context, ct));
        }).WithName("NetCoreAI.Tools.Test");

        tools.MapPost("/import-openapi", async (ImportRequest request, IToolService service, CancellationToken ct) =>
        {
            var imported = await service.ImportOpenApiAsync(request.Document, request.BaseUrl, ct);
            return Results.Ok(new { imported = imported.Count, tools = imported });
        }).WithName("NetCoreAI.Tools.ImportOpenApi");
    }

    /// <summary>
    /// Who is enabling in-process invocation, when the request is asking to.
    /// </summary>
    /// <remarks>
    /// Only passed when the definition actually asks for in-process and the endpoint was not already opted
    /// in in code — the service records it on the tool and logs it. An anonymous dashboard (a development
    /// host with <c>AllowAnonymous</c>) still names somebody, because "nobody enabled this" is not an answer
    /// anyone wants when reading the audit line back.
    /// </remarks>
    private static string? AllowInProcessBy(ToolDefinition tool, HttpContext http) =>
        tool.InvocationMode != ToolInvocationMode.InProcess
            ? null
            : http.User.Identity?.IsAuthenticated == true
                ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.Identity.Name ?? "an authenticated dashboard user"
                : "an anonymous dashboard user";

    /// <summary>A trial call: either the arguments to use, or a prompt to let the model choose them.</summary>
    public sealed record TestRequest
    {
        public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

        /// <summary>A sample question. When given, the model picks the arguments and they are shown back.</summary>
        public string? Prompt { get; init; }
    }

    /// <summary>An OpenAPI document to import, with the base URL its operations should be called against.</summary>
    public sealed record ImportRequest(string Document)
    {
        /// <summary>Overrides the document's own <c>servers</c> entry, which is often a placeholder.</summary>
        public string? BaseUrl { get; init; }
    }
}
