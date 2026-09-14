using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Providers;

namespace NetCoreAI.Dashboard.Api;

internal static class ProvidersApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var c = api.MapGroup("/providers/connections");

        c.MapGet("/", async (IConnectionManager manager, CancellationToken ct) =>
            Results.Ok((await manager.ListAsync(ct)).Select(Redact))).WithName("NetCoreAI.Connections.List");

        c.MapGet("/{id}", async (string id, IConnectionManager manager, CancellationToken ct) =>
        {
            var connection = await manager.GetAsync(id, ct);
            return connection is null ? Results.NotFound() : Results.Ok(Redact(connection));
        }).WithName("NetCoreAI.Connections.Get");

        c.MapPost("/", async (ConnectionInput input, IConnectionManager manager, CancellationToken ct) =>
        {
            try
            {
                var saved = await manager.SaveAsync(input.ToConnection(null), input.Secret, ct);
                return Results.Created($"{saved.Id}", Redact(saved));
            }
            catch (ProviderNotFoundException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithName("NetCoreAI.Connections.Create");

        c.MapPut("/{id}", async (string id, ConnectionInput input, IConnectionManager manager, CancellationToken ct) =>
        {
            var existing = await manager.GetAsync(id, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }

            var saved = await manager.SaveAsync(input.ToConnection(existing), input.Secret, ct);
            return Results.Ok(Redact(saved));
        }).WithName("NetCoreAI.Connections.Update");

        c.MapDelete("/{id}", async (string id, IConnectionManager manager, CancellationToken ct) =>
        {
            await manager.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Connections.Delete");

        c.MapPost("/{id}/test", async (string id, IConnectionManager manager, CancellationToken ct) =>
        {
            try
            {
                var r = await manager.TestAsync(id, ct);
                return Results.Ok(new { r.Success, Health = r.Health.ToString(), r.Message, r.Models, LatencyMs = (long)r.Latency.TotalMilliseconds });
            }
            catch (ConnectionNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("NetCoreAI.Connections.Test");

        c.MapPost("/{id}/models", async (string id, IConnectionManager manager, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await manager.SyncModelsAsync(id, ct));
            }
            catch (ConnectionNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Model listing failed");
            }
        }).WithName("NetCoreAI.Connections.SyncModels");
    }

    /// <summary>Never returns the protected secret; only whether one exists.</summary>
    private static object Redact(ProviderConnection c) => new
    {
        c.Id, c.Name, c.ProviderId, c.Preset, c.BaseUrl, c.Settings, c.DefaultParameters, c.Timeout, c.MaxRetries, c.MaxConcurrency,
        c.RateLimitPerMinute, c.CostPer1KInputTokens, c.CostPer1KOutputTokens, c.Enabled, c.LastHealthCheckAt, Health = c.Health.ToString(), c.HealthMessage, c.CreatedAt,
        HasSecret = !string.IsNullOrEmpty(c.ProtectedSecret),
    };

    public sealed record ConnectionInput(
        string Name,
        string ProviderId,
        string? Preset,
        string? BaseUrl,
        string? Secret,
        Dictionary<string, string>? Settings,
        ModelParameters? DefaultParameters,
        int? TimeoutSeconds,
        int? MaxRetries,
        int? MaxConcurrency,
        int? RateLimitPerMinute,
        decimal? CostPer1KInputTokens,
        decimal? CostPer1KOutputTokens,
        bool? Enabled)
    {
        public ProviderConnection ToConnection(ProviderConnection? existing) => new()
        {
            Id = existing?.Id ?? "",
            Name = Name,
            ProviderId = ProviderId,
            Preset = Preset ?? existing?.Preset,
            BaseUrl = BaseUrl ?? existing?.BaseUrl,
            ProtectedSecret = existing?.ProtectedSecret,
            Settings = Settings ?? existing?.Settings ?? new Dictionary<string, string>(),
            DefaultParameters = DefaultParameters ?? existing?.DefaultParameters ?? ModelParameters.Empty,
            Timeout = TimeoutSeconds is { } t ? TimeSpan.FromSeconds(t) : existing?.Timeout ?? TimeSpan.FromSeconds(120),
            MaxRetries = MaxRetries ?? existing?.MaxRetries ?? 2,
            MaxConcurrency = MaxConcurrency ?? existing?.MaxConcurrency ?? 8,
            RateLimitPerMinute = RateLimitPerMinute ?? existing?.RateLimitPerMinute,
            CostPer1KInputTokens = CostPer1KInputTokens ?? existing?.CostPer1KInputTokens,
            CostPer1KOutputTokens = CostPer1KOutputTokens ?? existing?.CostPer1KOutputTokens,
            Enabled = Enabled ?? existing?.Enabled ?? true,
            Health = existing?.Health ?? ConnectionHealth.Unknown,
            HealthMessage = existing?.HealthMessage,
            LastHealthCheckAt = existing?.LastHealthCheckAt,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
        };
    }
}
