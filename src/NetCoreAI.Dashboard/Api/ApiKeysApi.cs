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

        keys.MapPost("/", async (ApiKeyInput input, IApiKeyService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(input.ToKey(), ct);

            // The only time the secret exists outside the caller's hands. It is not stored, so this
            // response is the one chance to copy it.
            return Results.Ok(new { key = Redact(created.Key), secret = created.Secret });
        }).WithName("NetCoreAI.Keys.Create");

        keys.MapPut("/{id}", async (string id, ApiKeyInput input, IApiKeyService service, CancellationToken ct) =>
            Results.Ok(Redact(await service.UpdateAsync(input.ToKey(id), ct)))).WithName("NetCoreAI.Keys.Update");

        keys.MapDelete("/{id}", async (string id, IApiKeyService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Keys.Delete");
    }

    /// <summary>
    /// A key as a caller may describe it.
    /// </summary>
    /// <remarks>
    /// Not <see cref="ApiKey"/> itself: that record carries the hash and the prefix, which are the host's
    /// to set. Binding straight to it would have asked a caller to send a hash — meaningless at best, and
    /// an invitation to send a chosen one at worst — and in fact made the endpoint impossible to call,
    /// because those members are required.
    /// </remarks>
    public sealed record ApiKeyInput(string Name)
    {
        public bool Enabled { get; init; } = true;

        public IReadOnlyList<string> AgentIds { get; init; } = [];

        public IReadOnlyList<string> KnowledgeBaseIds { get; init; } = [];

        public IReadOnlyList<string> Claims { get; init; } = [];

        public int? RateLimitPerMinute { get; init; } = 120;

        public IReadOnlyList<string> IpAllowList { get; init; } = [];

        public DateTimeOffset? ExpiresAt { get; init; }

        /// <summary>The record to save. Hash and prefix are left empty for the service to fill in.</summary>
        public ApiKey ToKey(string? id = null) => new()
        {
            Id = id ?? string.Empty,
            Name = Name,
            Hash = string.Empty,
            Prefix = string.Empty,
            Enabled = Enabled,
            AgentIds = AgentIds,
            KnowledgeBaseIds = KnowledgeBaseIds,
            Claims = Claims,
            RateLimitPerMinute = RateLimitPerMinute,
            IpAllowList = IpAllowList,
            ExpiresAt = ExpiresAt,
        };
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
