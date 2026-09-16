using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Security;

namespace NetCoreAI.Dashboard.Api;

/// <summary>Reading the audit log. There is no way to write one from here, and no way to delete one.</summary>
internal static class AuditApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/audit", async (
            IAuditLog audit,
            string? entityType,
            string? entityId,
            string? actorId,
            string? action,
            int? days,
            int limit = 100,
            CancellationToken ct = default) =>
        {
            var entries = await audit.ListAsync(
                new AuditFilter
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    ActorId = actorId,
                    Action = action,
                    Since = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null,
                    Limit = limit,
                },
                ct);

            return Results.Ok(entries);
        }).WithName("NetCoreAI.Audit.List");
    }
}
