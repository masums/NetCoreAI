using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Settings;

namespace NetCoreAI.Dashboard.Api;

internal static class SettingsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/settings", async (ISettingsService settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)))
            .WithName("NetCoreAI.Settings.Get");

        api.MapPut("/settings", async (NetCoreAISettingsUpdate update, ISettingsService settings, CancellationToken ct) => Results.Ok(await settings.UpdateAsync(update, ct)))
            .WithName("NetCoreAI.Settings.Update");
    }
}
