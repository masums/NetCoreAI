using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Models;

namespace NetCoreAI.Dashboard.Api;

internal static class HardwareApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/hardware", async (IHardwareProbe probe, IModelLifecycleManager lifecycle, bool refresh = false, CancellationToken ct = default) =>
        {
            var info = await probe.ProbeAsync(refresh, ct);
            return Results.Ok(new { info, budgetAvailableBytes = lifecycle.AvailableBudgetBytes });
        }).WithName("NetCoreAI.Hardware");

        api.MapPost("/hardware/fit", async (FitRequest req, IModelRegistry registry, IFitEstimator estimator, CancellationToken ct) =>
        {
            var entry = await registry.GetAsync(req.ModelId, ct);
            if (entry is null)
            {
                return Results.NotFound();
            }

            var estimate = await estimator.EstimateAsync(entry.Descriptor, req.Options ?? LoadOptions.Default, ct);
            var recommended = await estimator.RecommendAsync(entry.Descriptor, ct);
            return Results.Ok(new { estimate, recommended });
        }).WithName("NetCoreAI.Hardware.Fit");
    }

    public sealed record FitRequest(string ModelId, LoadOptions? Options);
}
