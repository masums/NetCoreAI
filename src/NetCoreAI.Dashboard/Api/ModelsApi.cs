using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Providers;

namespace NetCoreAI.Dashboard.Api;

internal static class ModelsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var models = api.MapGroup("/models");

        models.MapGet("/", async (IModelRegistry registry, CancellationToken ct) =>
        {
            var list = await registry.ListAsync(ct);
            var aliases = await registry.GetAliasesAsync(ct);
            return Results.Ok(new
            {
                models = list.Select(e => ToDto(e, aliases)),
                aliases = aliases.Values,
            });
        }).WithName("NetCoreAI.Models.List");

        models.MapGet("/{id}", async (string id, IModelRegistry registry, CancellationToken ct) =>
        {
            var entry = await registry.GetAsync(id, ct);
            return entry is null ? Results.NotFound() : Results.Ok(ToDto(entry, await registry.GetAliasesAsync(ct)));
        }).WithName("NetCoreAI.Models.Get");

        models.MapPost("/", async (ModelDescriptor model, IModelRegistry registry, CancellationToken ct) =>
            Results.Ok(await registry.RegisterAsync(model, ct))).WithName("NetCoreAI.Models.Register");

        // Import: a path the server can read, or a URL queued through the download manager.
        models.MapPost("/import", async (ImportRequest request, IModelImporter importer, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.Path))
            {
                var imported = await importer.ImportFromPathAsync(request.Path, request.Name, request.Copy, ct);
                return Results.Ok(new { model = imported });
            }

            if (Uri.TryCreate(request.Url, UriKind.Absolute, out var url))
            {
                var job = await importer.ImportFromUrlAsync(url, request.Name, ct);
                return Results.Accepted($"downloads/{job.Id}", new { job });
            }

            return Results.BadRequest(new { error = "Give either a path on the server or an absolute http(s) URL." });
        }).WithName("NetCoreAI.Models.Import");

        models.MapPut("/{id}", async (string id, ModelUpdate update, IModelRegistry registry, CancellationToken ct) =>
        {
            var entry = await registry.GetAsync(id, ct);
            if (entry is null)
            {
                return Results.NotFound();
            }

            var d = entry.Descriptor;
            var updated = d with
            {
                Name = update.Name ?? d.Name,
                DefaultParameters = update.DefaultParameters ?? d.DefaultParameters,
                ChatTemplate = update.ChatTemplate ?? d.ChatTemplate,
                Tags = update.Tags ?? d.Tags,
                Notes = update.Notes ?? d.Notes,
                LoadOnStartup = update.LoadOnStartup ?? d.LoadOnStartup,
                ContextLength = update.ContextLength ?? d.ContextLength,
            };
            return Results.Ok(await registry.UpdateAsync(updated, ct));
        }).WithName("NetCoreAI.Models.Update");

        models.MapDelete("/{id}", async (string id, IModelRegistry registry, bool deleteFiles = false, CancellationToken ct = default) =>
        {
            try
            {
                await registry.RemoveAsync(id, deleteFiles, ct);
                return Results.NoContent();
            }
            catch (ModelNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("NetCoreAI.Models.Delete");

        models.MapPost("/{id}/load", async (string id, IModelRegistry registry, LoadOptions? options, CancellationToken ct) =>
        {
            try
            {
                var loaded = await registry.LoadAsync(id, options, ct);
                return Results.Ok(new { loaded.Descriptor.Id, loaded.MemoryBytes, loaded.LoadedAt });
            }
            catch (ModelNotFoundException)
            {
                return Results.NotFound();
            }
            catch (NetCoreAIException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: ex.GetType().Name);
            }
        }).WithName("NetCoreAI.Models.Load");

        models.MapPost("/{id}/unload", async (string id, IModelRegistry registry, CancellationToken ct) =>
        {
            try
            {
                await registry.UnloadAsync(id, ct);
                return Results.NoContent();
            }
            catch (ModelNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("NetCoreAI.Models.Unload");

        models.MapPut("/aliases/{alias}", async (string alias, AliasUpdate update, IModelRegistry registry, CancellationToken ct) =>
        {
            try
            {
                await registry.SetAliasAsync(alias, update.ModelId, update.FallbackModelIds, ct);
                return Results.NoContent();
            }
            catch (ModelNotFoundException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithName("NetCoreAI.Models.SetAlias");

        models.MapDelete("/aliases/{alias}", async (string alias, IModelRegistry registry, CancellationToken ct) =>
        {
            await registry.RemoveAliasAsync(alias, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Models.RemoveAlias");

        api.MapGet("/providers", (IProviderRegistry providers) => Results.Ok(providers.All.Select(p => new
        {
            p.Id,
            p.DisplayName,
            Kind = p.Kind.ToString(),
            Formats = p.SupportedFormats.Select(f => f.ToString()),
            Enabled = providers.IsEnabled(p),
            Presets = (p as IConnectionAwareProvider)?.Presets ?? [],
        }))).WithName("NetCoreAI.Providers.List");
    }

    internal static object ToDto(ModelEntry e, IReadOnlyDictionary<string, ModelAlias> aliases) => new
    {
        e.Descriptor,
        Status = e.Status.ToString(),
        e.StatusMessage,
        Aliases = aliases.Values.Where(a => a.ModelId == e.Descriptor.Id).Select(a => a.Alias).ToArray(),
        MemoryBytes = e.Loaded?.MemoryBytes,
        LoadedAt = e.Loaded?.LoadedAt,
    };

    /// <summary>Import a model the server can already reach: a local path, or a direct URL.</summary>
    /// <param name="Path">File or folder on the server's filesystem.</param>
    /// <param name="Url">Direct http(s) link to a model file.</param>
    /// <param name="Name">Display name; defaults to the file or folder name.</param>
    /// <param name="Copy">Copy the files into the data directory instead of registering them where they are.</param>
    public sealed record ImportRequest(string? Path, string? Url, string? Name, bool Copy = false);

    public sealed record ModelUpdate(string? Name, ModelParameters? DefaultParameters, string? ChatTemplate, IReadOnlyList<string>? Tags, string? Notes, bool? LoadOnStartup, int? ContextLength);

    public sealed record AliasUpdate(string ModelId, IReadOnlyList<string>? FallbackModelIds);
}
