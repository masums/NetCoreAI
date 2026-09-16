using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Telemetry;

namespace NetCoreAI.Dashboard.Api;

/// <summary>What has been run, what it used, and what it cost.</summary>
internal static class UsageApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var usage = api.MapGroup("/usage");

        usage.MapGet("/", async (
            IUsageAnalytics analytics,
            string? agentId,
            string? modelId,
            string? userId,
            bool? success,
            int days = 30,
            CancellationToken ct = default) =>
            Results.Ok(await analytics.SummariseAsync(Query(agentId, modelId, userId, success, days, null, 0, 0), ct)))
            .WithName("NetCoreAI.Usage.Summary");

        usage.MapGet("/runs", async (
            IUsageAnalytics analytics,
            string? agentId,
            string? modelId,
            string? userId,
            bool? success,
            string? search,
            int days = 30,
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default) =>
        {
            var (runs, total) = await analytics.BrowseAsync(Query(agentId, modelId, userId, success, days, search, limit, offset), ct);
            return Results.Ok(new { total, runs });
        }).WithName("NetCoreAI.Usage.Runs");

        usage.MapGet("/export", async (
            IUsageAnalytics analytics,
            string? agentId,
            string? modelId,
            string? userId,
            bool? success,
            int days = 30,
            CancellationToken ct = default) =>
        {
            var csv = await analytics.ExportCsvAsync(Query(agentId, modelId, userId, success, days, null, 500, 0), ct);
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(csv),
                "text/csv",
                $"netcoreai-usage-{DateTimeOffset.UtcNow:yyyyMMdd}.csv");
        }).WithName("NetCoreAI.Usage.Export");
    }

    private static RunQuery Query(string? agentId, string? modelId, string? userId, bool? success, int days, string? search, int limit, int offset) =>
        new()
        {
            AgentId = agentId,
            ModelId = modelId,
            UserId = userId,
            Success = success,
            Search = search,

            // 0 days means everything kept, which is however far back retention allows.
            Since = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days) : null,
            Limit = limit <= 0 ? 50 : limit,
            Offset = offset,
        };
}
