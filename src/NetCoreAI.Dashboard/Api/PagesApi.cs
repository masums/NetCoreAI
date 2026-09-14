using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Dashboard.Components.Pages;
using NetCoreAI.Dashboard.Rendering;

namespace NetCoreAI.Dashboard.Api;

internal static class PagesApi
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", (HttpContext http, PageRenderer r) => Page<OverviewPage>(http, r, "")).ExcludeFromDescription();
        group.MapGet("/models", (HttpContext http, PageRenderer r) => Page<ModelsPage>(http, r, "models")).ExcludeFromDescription();
        group.MapGet("/hub", (HttpContext http, PageRenderer r) => Page<HubPage>(http, r, "hub")).ExcludeFromDescription();
        group.MapGet("/knowledge", (HttpContext http, PageRenderer r) => Page<KnowledgePage>(http, r, "knowledge")).ExcludeFromDescription();
        group.MapGet("/storage", (HttpContext http, PageRenderer r) => Page<StoragePage>(http, r, "storage")).ExcludeFromDescription();
        group.MapGet("/providers", (HttpContext http, PageRenderer r) => Page<ProvidersPage>(http, r, "providers")).ExcludeFromDescription();
        group.MapGet("/chat", (HttpContext http, PageRenderer r) => Page<ChatPage>(http, r, "chat")).ExcludeFromDescription();
        group.MapGet("/tools", (HttpContext http, PageRenderer r) => Page<ToolsPage>(http, r, "tools")).ExcludeFromDescription();
        group.MapGet("/agents", (HttpContext http, PageRenderer r) => Page<AgentsPage>(http, r, "agents")).ExcludeFromDescription();
        group.MapGet("/hardware", (HttpContext http, PageRenderer r) => Page<HardwarePage>(http, r, "hardware")).ExcludeFromDescription();
        group.MapGet("/settings", (HttpContext http, PageRenderer r) => Page<SettingsPage>(http, r, "settings")).ExcludeFromDescription();
    }

    private static async Task<IResult> Page<TPage>(HttpContext http, PageRenderer renderer, string current) where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        var html = await renderer.RenderAsync<TPage>(http, current);
        return Results.Content(html, "text/html; charset=utf-8");
    }
}

/// <summary>Shown when the dashboard is mounted without any authorization configured (default deny).</summary>
internal static class ForbiddenPage
{
    public const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>NetCoreAI: access denied</title>
        <style>body{font-family:system-ui,sans-serif;max-width:720px;margin:4rem auto;padding:0 1rem;color:#222}code,pre{background:#f4f4f5;border-radius:4px;padding:.15rem .35rem}pre{padding:1rem;overflow:auto}</style></head>
        <body><h1>NetCoreAI dashboard: access denied</h1>
        <p>The dashboard is mounted but no authorization policy is configured, so every request is refused (default deny).</p>
        <p>Choose one in <code>Program.cs</code>:</p>
        <pre>builder.Services.AddNetCoreAI(o =&gt;
        {
            o.Dashboard.Authorization = p =&gt; p.RequireRole("Admin");   // host roles / claims
            // or: o.Dashboard.PolicyName = "MyPolicy";
            // or, for local development only: o.Dashboard.AllowAnonymous = true;
        });</pre>
        <p>Roles <code>NetCoreAI.Admin</code>, <code>NetCoreAI.Builder</code> and <code>NetCoreAI.User</code> further restrict what an authorized user can do.</p>
        </body></html>
        """;
}
