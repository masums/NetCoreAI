using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Storage;

namespace NetCoreAI.Dashboard.Api;

/// <summary>
/// Disk accounting for the data directory: what each model costs, what is left over, and reclaiming it.
/// </summary>
internal static class StorageApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/storage", async (IStorageService storage, CancellationToken ct) =>
            Results.Ok(await storage.GetUsageAsync(ct)))
            .WithName("NetCoreAI.Storage.Usage");

        api.MapGet("/storage/orphans", async (IStorageService storage, CancellationToken ct) =>
            Results.Ok(await storage.ScanOrphansAsync(ct)))
            .WithName("NetCoreAI.Storage.Orphans");

        api.MapPost("/storage/orphans/delete", async (DeleteOrphansRequest request, IStorageService storage, CancellationToken ct) =>
        {
            if (request.Paths is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "Pick at least one file to delete." });
            }

            var freed = await storage.DeleteOrphansAsync(request.Paths, ct);
            return Results.Ok(new { freedBytes = freed, count = request.Paths.Count });
        }).WithName("NetCoreAI.Storage.DeleteOrphans");
    }

    /// <summary>Paths to delete; each is re-checked against the registry before anything is removed.</summary>
    public sealed record DeleteOrphansRequest(IReadOnlyList<string> Paths);
}
