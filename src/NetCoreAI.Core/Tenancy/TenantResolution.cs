using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Tenancy;

/// <summary>Works out which tenant a request belongs to.</summary>
/// <remarks>
/// Implement this when none of the built-in resolvers fits — a tenant read from a path segment, a
/// subscription looked up in your own database, a mapping held in cache. Return null to refuse the
/// request: there is no fallback to a default, because a request whose tenant could not be established is
/// a request that must not read anybody's data.
/// </remarks>
public interface ITenantResolver
{
    ValueTask<string?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default);
}

/// <summary>How tenants are identified, and what happens when one cannot be.</summary>
public sealed class TenancyOptions
{
    /// <summary>
    /// Serve more than one tenant. Off by default, and switching it on changes nothing about how data is
    /// stored — a single-tenant host is already one tenant, so there is no migration.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Claim carrying the tenant id, when tenants come from the signed-in identity.</summary>
    public string? ClaimType { get; set; }

    /// <summary>Request header carrying the tenant id, for service-to-service callers.</summary>
    public string? Header { get; set; }

    /// <summary>
    /// Read the tenant from the leftmost label of the host name, so <c>acme.example.com</c> is
    /// <c>acme</c>.
    /// </summary>
    public bool FromSubdomain { get; set; }

    /// <summary>
    /// Host names that are not a tenant — the bare domain, a load balancer's health probe, localhost.
    /// Only consulted when <see cref="FromSubdomain"/> is on.
    /// </summary>
    public IList<string> IgnoredHosts { get; } = ["localhost", "127.0.0.1", "www"];

    /// <summary>
    /// Create a tenant the first time one is seen.
    /// </summary>
    /// <remarks>
    /// Convenient when tenants come from a trusted identity provider and the host has no separate
    /// sign-up. Dangerous when they come from a header, because then anybody who can reach the host can
    /// bring a tenant into existence.
    /// </remarks>
    public bool CreateOnFirstUse { get; set; }
}

/// <summary>Reads the tenant from a claim on the signed-in identity.</summary>
internal sealed class ClaimTenantResolver(IOptionsMonitor<TenancyOptions> options) : ITenantResolver
{
    public ValueTask<string?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var claim = options.CurrentValue.ClaimType;
        return ValueTask.FromResult(claim is { Length: > 0 } ? context.User.FindFirstValue(claim) : null);
    }
}

/// <summary>
/// Reads the tenant from a request header.
/// </summary>
/// <remarks>
/// A header is whatever the caller puts in it, so this is only safe where the caller is already trusted to
/// name a tenant — a gateway that sets it after authenticating, or a service call behind an API key scoped
/// to that tenant. Do not put this in front of the public internet.
/// </remarks>
internal sealed class HeaderTenantResolver(IOptionsMonitor<TenancyOptions> options) : ITenantResolver
{
    public ValueTask<string?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = options.CurrentValue.Header;
        return ValueTask.FromResult(
            header is { Length: > 0 } && context.Request.Headers.TryGetValue(header, out var value)
                ? value.ToString()
                : null);
    }
}

/// <summary>Reads the tenant from the first label of the host name.</summary>
internal sealed class SubdomainTenantResolver(IOptionsMonitor<TenancyOptions> options) : ITenantResolver
{
    public ValueTask<string?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = options.CurrentValue;
        if (!settings.FromSubdomain || context.Request.Host.Host is not { Length: > 0 } host)
        {
            return ValueTask.FromResult<string?>(null);
        }

        var label = host.Split('.')[0];
        return ValueTask.FromResult<string?>(
            settings.IgnoredHosts.Contains(label, StringComparer.OrdinalIgnoreCase) ? null : label);
    }
}

/// <summary>
/// Puts the request's tenant where the store can see it.
/// </summary>
/// <remarks>
/// <para>
/// An endpoint filter rather than middleware, so it runs after authentication: a tenant read from a claim
/// needs the claim to exist by then.
/// </para>
/// <para>
/// Every registered resolver is tried in order and the first answer wins, so a host can read a claim when
/// somebody is signed in and fall back to a header for service calls. A request whose tenant cannot be
/// established is refused rather than served as the default tenant — serving it would hand one caller
/// another's data, which is the only failure in this whole feature that matters.
/// </para>
/// </remarks>
public static class TenantScope
{
    /// <summary>Wraps a route group so everything under it runs as the caller's tenant.</summary>
    public static TBuilder UseTenancy<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var options = http.RequestServices.GetRequiredService<IOptionsMonitor<TenancyOptions>>();
            if (!options.CurrentValue.Enabled)
            {
                return await next(context).ConfigureAwait(false);
            }

            var accessor = http.RequestServices.GetRequiredService<ITenantAccessor>();
            var tenants = http.RequestServices.GetRequiredService<ITenantService>();

            string? resolved = null;
            foreach (var resolver in http.RequestServices.GetServices<ITenantResolver>())
            {
                resolved = await resolver.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false);
                if (resolved is { Length: > 0 })
                {
                    break;
                }
            }

            if (!TenantId.IsValid(resolved))
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(TenantScope))
                    .LogWarning("No tenant could be established for {Path}; the request was refused.", http.Request.Path);

                return Results.Problem(
                    "This host serves several tenants and could not tell which one this request is for.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "No tenant");
            }

            var tenant = await tenants.GetAsync(resolved!, http.RequestAborted).ConfigureAwait(false);
            if (tenant is null && options.CurrentValue.CreateOnFirstUse)
            {
                tenant = await tenants.CreateAsync(new Tenant { Id = resolved!, Name = resolved! }, http.RequestAborted).ConfigureAwait(false);
            }

            if (tenant is null or { Enabled: false })
            {
                // Worded the same either way. "No such tenant" tells somebody probing which ids exist, and
                // "that tenant is disabled" tells them the same thing more politely.
                return Results.Problem(
                    "This tenant cannot be served.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Tenant unavailable");
            }

            using var scope = accessor.Use(tenant.Id);
            return await next(context).ConfigureAwait(false);
        });

        return builder;
    }
}
