using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Dashboard.Components;

namespace NetCoreAI.Dashboard.Rendering;

/// <summary>Per-request information every page needs.</summary>
public sealed record DashboardContext(string BasePath, string CurrentPath, string Title, string? UserName, bool IsAdmin, bool IsBuilder)
{
    public string Url(string relative) => BasePath.TrimEnd('/') + "/" + relative.TrimStart('/');

    public string Asset(string file) => Url("_content/" + file);

    public bool IsActive(string relative) => string.Equals(CurrentPath.Trim('/'), relative.Trim('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Renders Razor components to static HTML with <see cref="HtmlRenderer"/> (no Blazor circuit, no host middleware).
/// Interactive parts use plain fetch/SSE against the management API.
/// </summary>
public sealed class PageRenderer(IServiceProvider services, ILoggerFactory loggerFactory, IOptionsMonitor<NetCoreAIOptions> options)
{
    public async Task<string> RenderAsync<TPage>(HttpContext http, string currentPath, IDictionary<string, object?>? parameters = null) where TPage : IComponent
    {
        await using var scope = services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, loggerFactory);
        var ctx = CreateContext(http, currentPath);
        var pageParameters = new Dictionary<string, object?>(parameters ?? new Dictionary<string, object?>()) { ["Ctx"] = ctx };

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var shellParameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Ctx"] = ctx,
                ["PageType"] = typeof(TPage),
                ["PageParameters"] = pageParameters,
            });
            var output = await renderer.RenderComponentAsync<Shell>(shellParameters);
            return output.ToHtmlString();
        });
    }

    public DashboardContext CreateContext(HttpContext http, string currentPath)
    {
        var o = options.CurrentValue;
        var user = http.User;
        var roleClaim = o.Dashboard.RoleClaimType ?? ClaimTypes.Role;
        bool HasRole(string role) => user.Identity?.IsAuthenticated == true && (user.IsInRole(role) || user.HasClaim(roleClaim, role));
        var isAdmin = o.Dashboard.AllowAnonymous || HasRole(NetCoreAIRoles.Admin) || !user.Claims.Any(c => c.Type == roleClaim);
        return new DashboardContext(
            http.Request.PathBase.Add(o.Dashboard.Path).Value ?? o.Dashboard.Path,
            currentPath,
            o.Dashboard.Title,
            user.Identity?.Name,
            isAdmin,
            isAdmin || HasRole(NetCoreAIRoles.Builder));
    }
}

/// <summary>Role names mapped to host roles or claims (see DashboardOptions.RoleClaimType).</summary>
public static class NetCoreAIRoles
{
    public const string Admin = "NetCoreAI.Admin";
    public const string Builder = "NetCoreAI.Builder";
    public const string User = "NetCoreAI.User";
}
