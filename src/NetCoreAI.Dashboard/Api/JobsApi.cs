using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NetCoreAI.Dashboard.Api;

/// <summary>
/// Background jobs: ingestion runs today, agent runs in Phase 3. Progress and failures are read from the
/// persisted records, so a job that was interrupted still shows what it managed to do.
/// </summary>
internal static class JobsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var jobs = api.MapGroup("/jobs");

        jobs.MapGet("/", async (IBackgroundJobRunner runner, string? targetId = null, CancellationToken ct = default) =>
            Results.Ok(await runner.ListAsync(targetId, ct))).WithName("NetCoreAI.Jobs.List");

        jobs.MapGet("/{id}", async (string id, IBackgroundJobRunner runner, CancellationToken ct) =>
            await runner.GetAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound())
            .WithName("NetCoreAI.Jobs.Get");

        jobs.MapPost("/{id}/cancel", async (string id, IBackgroundJobRunner runner, CancellationToken ct) =>
        {
            await runner.CancelAsync(id, ct);
            return await runner.GetAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound();
        }).WithName("NetCoreAI.Jobs.Cancel");

        jobs.MapPost("/{id}/retry", async (string id, IBackgroundJobRunner runner, CancellationToken ct) =>
            Results.Ok(await runner.RetryAsync(id, ct))).WithName("NetCoreAI.Jobs.Retry");
    }
}
