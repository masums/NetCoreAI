using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Alerts;

namespace NetCoreAI.Dashboard.Api;

/// <summary>What NetCoreAI has raised, and a way to prove the wiring works.</summary>
internal static class AlertsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var alerts = api.MapGroup("/alerts");

        alerts.MapGet("/", (IAlertService service, int limit = 50) =>
            Results.Ok(service.Recent(limit))).WithName("NetCoreAI.Alerts.List");

        alerts.MapPost("/test", async (IAlertService service, CancellationToken ct) =>
        {
            // A test alert carries its own timestamp in the key, so it is never suppressed as a repeat —
            // the one alert that must always arrive is the one you sent to find out whether they arrive.
            await service.RaiseAsync(
                new Alert
                {
                    Key = $"test:{DateTimeOffset.UtcNow.UtcTicks}",
                    Severity = AlertSeverity.Info,
                    Title = "This is a test alert from NetCoreAI.",
                    Detail = "If you are reading this somewhere other than the log, your alert sink works.",
                },
                ct);

            return Results.Accepted();
        }).WithName("NetCoreAI.Alerts.Test");
    }
}
