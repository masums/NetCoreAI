using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using NetCoreAI.Storage;

namespace NetCoreAI.Dashboard.Api;

/// <summary>Taking a copy of what is here, and putting one back.</summary>
internal static class BackupApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Map(RouteGroupBuilder api)
    {
        var bundles = api.MapGroup("/bundles");

        bundles.MapGet("/export", async (
            IBundleService service,
            string? agents,
            string? description,
            CancellationToken ct) =>
        {
            var ids = agents is { Length: > 0 }
                ? agents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;

            var bundle = await service.ExportAsync(ids, description, ct);

            return Results.File(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle, Json)),
                "application/json",
                $"netcoreai-bundle-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.json");
        }).WithName("NetCoreAI.Bundles.Export");

        bundles.MapPost("/import", async (Bundle bundle, IBundleService service, ImportMode mode = ImportMode.Validate, CancellationToken ct = default) =>
            Results.Ok(await service.ImportAsync(bundle, mode, ct))).WithName("NetCoreAI.Bundles.Import");

        // A snapshot of the metadata database. Not the whole data directory: models and uploaded files are
        // large, already on disk, and a backup route that copied them would time out on any real host.
        api.MapPost("/backup", async (
            IMetadataStore store,
            IOptions<NetCoreAIOptions> options,
            CancellationToken ct) =>
        {
            if (store is not ISnapshotSource snapshot)
            {
                return Results.Problem(
                    "This metadata store cannot take a consistent snapshot while it is running. Stop the host and copy its files, or use a store that can.",
                    statusCode: StatusCodes.Status501NotImplemented,
                    title: "No snapshot available");
            }

            var folder = Path.Combine(options.Value.DataDirectory, "backups");
            var path = Path.Combine(folder, $"netcoreai-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db");

            await snapshot.SnapshotAsync(path, ct);

            return Results.Ok(new { path, bytes = new FileInfo(path).Length });
        }).WithName("NetCoreAI.Backup");
    }
}
