using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCoreAI.Dashboard.Api;
using NetCoreAI.Dashboard.Rendering;
using NetCoreAI.Hub;

namespace NetCoreAI;

public static class NetCoreAIEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the dashboard, management API and health endpoint under <see cref="DashboardOptions.Path"/> (default /netcoreai).
    /// Nothing outside that prefix is touched. Requires <c>AddNetCoreAI()</c>.
    /// </summary>
    public static RouteGroupBuilder MapNetCoreAI(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var services = endpoints.ServiceProvider;
        if (services.GetService<NetCoreAIServiceCollectionExtensions.NetCoreAIMarker>() is null)
        {
            throw new InvalidOperationException("Call builder.Services.AddNetCoreAI() before app.MapNetCoreAI().");
        }

        if (services.GetService<PageRenderer>() is null)
        {
            throw new InvalidOperationException("NetCoreAI.Dashboard services are missing. Reference the NetCoreAI meta-package, or call AddNetCoreAI().AddNetCoreAIDashboard().");
        }

        var options = services.GetRequiredService<IOptions<NetCoreAIOptions>>().Value;
        var path = "/" + options.Dashboard.Path.Trim('/');
        var group = endpoints.MapGroup(path).WithGroupName("netcoreai");

        ApplyAuthorization(group, options.Dashboard);

        // Health is intentionally outside the authorization policy so load balancers can probe it.
        endpoints.MapHealthChecks(path + "/health", new HealthCheckOptions { ResponseWriter = HealthApi.WriteAsync }).AllowAnonymous();

        group.MapGet("/_content/{**file}", (string file, EmbeddedAssets assets, HttpContext http) =>
        {
            if (!assets.TryGet(file, out var stream, out var contentType))
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "public,max-age=86400";
            return Results.Stream(stream!, contentType);
        }).AllowAnonymous().ExcludeFromDescription();

        PagesApi.Map(group);
        OpenApiDocument.Map(group, path);
        var api = group.MapGroup("/api");

        // One place turns a NetCoreAI failure into an HTTP response. Without it these surface as unhandled
        // exceptions, which means a 500 and a stack trace where the caller should have been told what to fix.
        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (OfflineModeException ex)
            {
                // The request is valid; the host is configured not to allow it right now.
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Offline mode");
            }
            catch (NetCoreAIException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "NetCoreAI");
            }
        });
        HardwareApi.Map(api);
        ModelsApi.Map(api);
        HubApi.Map(api);
        StorageApi.Map(api);
        KnowledgeApi.Map(api);
        JobsApi.Map(api);
        ToolsApi.Map(api);
        AgentsApi.Map(api);
        ApiKeysApi.Map(api);
        AuditApi.Map(api);
        ProvidersApi.Map(api);
        ChatApi.Map(api);
        SettingsApi.Map(api);
        return group;
    }

    private static void ApplyAuthorization(RouteGroupBuilder group, DashboardOptions dashboard)
    {
        if (dashboard.AllowAnonymous)
        {
            group.AllowAnonymous();
            return;
        }

        if (!string.IsNullOrEmpty(dashboard.PolicyName))
        {
            group.RequireAuthorization(dashboard.PolicyName);
            return;
        }

        if (dashboard.Authorization is not null)
        {
            var builder = new AuthorizationPolicyBuilder();
            dashboard.Authorization(builder);
            group.RequireAuthorization(builder.Build());
            return;
        }

        // Default deny with an explanation instead of a bare 403 or a login redirect loop.
        group.AddEndpointFilter((ctx, next) =>
            ValueTask.FromResult<object?>(Results.Content(ForbiddenPage.Html, "text/html; charset=utf-8", statusCode: StatusCodes.Status403Forbidden)));
    }
}
