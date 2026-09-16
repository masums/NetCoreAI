using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NetCoreAI.Models;

namespace NetCoreAI.Dashboard.Api;

internal static class HealthApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task WriteAsync(HttpContext http, HealthReport report)
    {
        var lifecycle = http.RequestServices.GetService(typeof(IModelLifecycleManager)) as IModelLifecycleManager;
        var store = http.RequestServices.GetService(typeof(IMetadataStore)) as IMetadataStore;
        var options = (http.RequestServices.GetService(typeof(IOptions<NetCoreAIOptions>)) as IOptions<NetCoreAIOptions>)?.Value;

        var storeOk = store is not null && await store.IsHealthyAsync(http.RequestAborted);
        long? freeBytes = null;
        try
        {
            if (options is not null && Directory.Exists(options.DataDirectory))
            {
                freeBytes = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataDirectory))!).AvailableFreeSpace;
            }
        }
        catch (IOException)
        {
        }

        var lowDisk = freeBytes is < 2L * 1024 * 1024 * 1024;
        var status = !storeOk ? "Unhealthy" : lowDisk || report.Status == HealthStatus.Degraded ? "Degraded" : "Healthy";
        http.Response.StatusCode = status == "Unhealthy" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status,
            ready = lifecycle?.IsReady ?? false,
            loadedModels = lifecycle?.Loaded.Select(m => m.Descriptor.Id).ToArray() ?? [],
            metadataStore = storeOk ? "ok" : "unreachable",

            // The data directory is deliberately not reported. This endpoint is anonymous — it has to be,
            // because a load balancer probes it before anyone has signed in — and an absolute filesystem
            // path tells an unauthenticated caller the account name, the deployment layout and where the
            // database sits. The free space below is the health signal; the path it was measured on is not
            // part of it. Signed-in administrators see the path on the storage page.
            freeDiskBytes = freeBytes,
            host = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        }, Json));
    }
}
