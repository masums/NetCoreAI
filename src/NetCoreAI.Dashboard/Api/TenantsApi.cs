using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Tenancy;

namespace NetCoreAI.Dashboard.Api;

/// <summary>Tenants, their limits, and what they are using.</summary>
internal static class TenantsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var tenants = api.MapGroup("/tenants");

        tenants.MapGet("/", async (ITenantService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct))).WithName("NetCoreAI.Tenants.List");

        tenants.MapGet("/{id}", async (string id, ITenantService service, CancellationToken ct) =>
        {
            var tenant = await service.GetAsync(id, ct);
            return tenant is null ? Results.NotFound() : Results.Ok(tenant);
        }).WithName("NetCoreAI.Tenants.Get");

        tenants.MapPost("/", async (Tenant tenant, ITenantService service, CancellationToken ct) =>
            Results.Ok(await service.CreateAsync(tenant, ct))).WithName("NetCoreAI.Tenants.Create");

        tenants.MapPut("/{id}", async (string id, Tenant tenant, ITenantService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(tenant with { Id = id }, ct))).WithName("NetCoreAI.Tenants.Update");

        // The caller's own usage, not an arbitrary tenant's: reading it for somebody else would mean
        // stepping outside the tenant the request established, which is the one thing nothing here does.
        tenants.MapGet("/usage", async (ITenantQuotas quotas, ITenantAccessor accessor, CancellationToken ct) =>
            Results.Ok(new { tenant = accessor.Current, usage = await quotas.UsageAsync(ct) }))
            .WithName("NetCoreAI.Tenants.Usage");
    }
}
