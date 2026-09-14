using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Agents;
using NetCoreAI.Security;

namespace NetCoreAI.Dashboard.Api;

/// <summary>Agents: defining them, running them, and reading what a run did.</summary>
internal static class AgentsApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(RouteGroupBuilder api)
    {
        var agents = api.MapGroup("/agents");

        agents.MapGet("/", async (IAgentService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct))).WithName("NetCoreAI.Agents.List");

        agents.MapGet("/{id}", async (string id, IAgentService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } agent ? Results.Ok(agent) : Results.NotFound()).WithName("NetCoreAI.Agents.Get");

        agents.MapPost("/", async (AgentDefinition agent, IAgentService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(agent, ct))).WithName("NetCoreAI.Agents.Create");

        agents.MapPut("/{id}", async (string id, AgentDefinition agent, IAgentService service, CancellationToken ct) =>
            id != agent.Id
                ? Results.BadRequest(new { error = "The id in the URL and the body must match." })
                : Results.Ok(await service.SaveAsync(agent, ct))).WithName("NetCoreAI.Agents.Update");

        agents.MapDelete("/{id}", async (string id, IAgentService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Agents.Delete");

        agents.MapPost("/{id}/run", async (string id, AgentRequest request, IAgentService service, HttpContext http, CancellationToken ct) =>
            Refused(http, id) ?? Results.Ok(await service.RunAsync(id, request, Caller(http), ct))).WithName("NetCoreAI.Agents.Run");

        // Server-Sent Events: delta*, citations, step*, done | error. Same shape the chat playground uses,
        // so one client can read either.
        agents.MapPost("/{id}/run/stream", async (string id, AgentRequest request, IAgentService service, HttpContext http, CancellationToken ct) =>
        {
            if (Refused(http, id) is { } refusal)
            {
                // Checked before the stream starts: a refusal written as an SSE event would be read by a
                // client as an answer that happened to fail, rather than as a call that was never made.
                return refusal;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.StartAsync(ct);

            try
            {
                await foreach (var evt in service.RunStreamingAsync(id, request, Caller(http), ct))
                {
                    await http.Response.WriteAsync($"event: {evt.Type}\ndata: {JsonSerializer.Serialize(evt, Json)}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (NetCoreAIException ex)
            {
                await http.Response.WriteAsync(
                    $"event: error\ndata: {JsonSerializer.Serialize(new AgentEvent(AgentEvent.ErrorType) { Error = ex.Message }, Json)}\n\n",
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // The caller went away mid-run; the trace is already written.
            }

            return Results.Empty;
        }).WithName("NetCoreAI.Agents.RunStream");

        agents.MapGet("/{id}/runs", async (string id, IAgentService service, int limit = 50, CancellationToken ct = default) =>
            Results.Ok(await service.ListRunsAsync(id, limit, ct))).WithName("NetCoreAI.Agents.Runs");

        api.MapGet("/runs/{runId}", async (string runId, IAgentService service, CancellationToken ct) =>
            await service.GetRunAsync(runId, ct) is { } run ? Results.Ok(run) : Results.NotFound()).WithName("NetCoreAI.Runs.Get");
    }

    /// <summary>
    /// Whether an API key scoped to particular agents may run this one.
    /// </summary>
    /// <remarks>
    /// Only applies when the caller presented a key. A person signed in to the dashboard is governed by
    /// the dashboard's own authorization and by the agent's access tags, not by a key's scopes.
    /// </remarks>
    private static IResult? Refused(HttpContext http, string agentId) =>
        http.ApiKey() is { } key && !key.CanRun(agentId)
            ? Results.Problem(
                $"This API key is not scoped to run '{agentId}'.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Out of scope")
            : null;

    /// <summary>
    /// The caller a run acts as.
    /// </summary>
    /// <remarks>
    /// Taken from the request rather than configured: an agent's tools and its retrieval both run as
    /// whoever asked, and the address here is the one the host is actually reached on, which is what a
    /// loopback tool call needs behind a proxy.
    /// </remarks>
    private static AgentCaller Caller(HttpContext http) => new()
    {
        User = http.User,
        UserId = http.User.Identity?.IsAuthenticated == true
            ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.Identity.Name
            : null,
        BaseAddress = new Uri($"{http.Request.Scheme}://{http.Request.Host}"),
        AuthorizationHeader = http.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null,
    };
}
