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

        tools.MapGet("/discover", (IEndpointDiscovery discovery) =>
        {
            var endpoints = discovery.Discover();
            return Results.Ok(new
            {
                endpoints,

                // The designer leads with the endpoints whose author already said they were safe to expose.
                optedIn = endpoints.Count(e => e.OptedIn),
            });
        }).WithName("NetCoreAI.Tools.Discover");
    }
}
