using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Security;

namespace NetCoreAI.Dashboard.Api;

/// <summary>API keys: issuing them, scoping them, and taking them away.</summary>
internal static class ApiKeysApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/keys");

        keys.MapGet("/", async (IApiKeyService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(Redact))).WithName("NetCoreAI.Keys.List");

        keys.MapPost("/", async (ApiKey key, IApiKeyService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(key, ct);

            // The only time the secret exists outside the caller's hands. It is not stored, so this
            // response is the one chance to copy it.
            return Results.Ok(new { key = Redact(created.Key), secret = created.Secret });
        }).WithName("NetCoreAI.Keys.Create");

        keys.MapPut("/{id}", async (string id, ApiKey key, IApiKeyService service, CancellationToken ct) =>
            id != key.Id
                ? Results.BadRequest(new { error = "The id in the URL and the body must match." })
                : Results.Ok(Redact(await service.UpdateAsync(key, ct)))).WithName("NetCoreAI.Keys.Update");

        keys.MapDelete("/{id}", async (string id, IApiKeyService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Keys.Delete");
    }

    /// <summary>
    /// A key as the API returns it.
    /// </summary>
    /// <remarks>
    /// The hash never leaves the host. It is not the secret, but it is the only thing standing between a
    /// leaked backup and a working key, and nothing in a dashboard needs it.
    /// </remarks>
    private static object Redact(ApiKey key) => new
    {
        key.Id,
        key.Name,
        key.Prefix,
        key.Enabled,
        key.AgentIds,
        key.KnowledgeBaseIds,
        key.Claims,
        key.RateLimitPerMinute,
        key.IpAllowList,
        key.CreatedAt,
        key.ExpiresAt,
        key.LastUsedAt,
    };
}
