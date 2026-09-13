using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Dashboard.Rendering;
using NetCoreAI.Hub;

namespace NetCoreAI.Dashboard.Api;

/// <summary>
/// Browsing the model hub and driving the download queue. Every hub call is made by the server, so the
/// Hugging Face token never reaches the browser and offline mode is enforced in one place.
/// </summary>
internal static class HubApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/hub/sources", (IHubService hub) =>
            Results.Ok(hub.Sources.Select(s => new { s.Id, s.DisplayName })))
            .WithName("NetCoreAI.Hub.Sources");

        api.MapGet("/hub/search", async (
            IHubService hub,
            string? q = null,
            string? author = null,
            string? task = null,
            ModelFormat? format = null,
            string? license = null,
            ModelSearchSort sort = ModelSearchSort.Downloads,
            int limit = 30,
            int offset = 0,
            string? source = null,
            CancellationToken ct = default) =>
        {
            var query = new ModelSearchQuery
            {
                Text = q,
                Author = author,
                Task = task,
                Format = format,
                License = license,
                Sort = sort,
                Limit = Math.Clamp(limit, 1, 100),
                Offset = Math.Max(0, offset),
            };

            return Results.Ok(await hub.SearchAsync(query, source, ct));
        }).WithName("NetCoreAI.Hub.Search");

        api.MapGet("/hub/recommended", async (IHubService hub, bool refresh = false, CancellationToken ct = default) =>
            Results.Ok(await hub.GetRecommendedAsync(refresh, ct)))
            .WithName("NetCoreAI.Hub.Recommended");

        // The repo id carries a slash ("owner/name"), so it is matched as a catch-all rather than a segment.
        api.MapGet("/hub/models/{**repoId}", async (
            string repoId,
            IHubService hub,
            Microsoft.Extensions.Options.IOptionsMonitor<NetCoreAIOptions> options,
            string? source = null,
            string? revision = null,
            CancellationToken ct = default) =>
        {
            var view = await hub.GetAsync(repoId, source, revision, ct);
            if (view is null)
            {
                return Results.NotFound();
            }

            // Rendered here, with raw HTML stripped: a model card is a stranger's markdown.
            var readmeBase = $"{options.CurrentValue.Network.HuggingFaceEndpoint.TrimEnd('/')}/{repoId}/resolve/{revision ?? "main"}";
            return Results.Ok(new
            {
                view.Detail,
                view.Variants,
                ReadmeHtml = MarkdownRenderer.ToHtml(view.Detail.ReadmeMarkdown, readmeBase),
            });
        }).WithName("NetCoreAI.Hub.Model");

        MapDownloads(api);
    }

    private static void MapDownloads(RouteGroupBuilder api)
    {
        api.MapGet("/downloads", async (IDownloadManager downloads, CancellationToken ct) =>
            Results.Ok(await downloads.ListAsync(ct)))
            .WithName("NetCoreAI.Downloads.List");

        api.MapGet("/downloads/{id}", async (string id, IDownloadManager downloads, CancellationToken ct) =>
            await downloads.GetAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound())
            .WithName("NetCoreAI.Downloads.Get");

        api.MapGet("/downloads/events", async (HttpContext http, IDownloadManager downloads, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // The manager raises Progress from its worker thread; the channel hands them to this request.
            var channel = System.Threading.Channels.Channel.CreateBounded<DownloadJob>(
                new System.Threading.Channels.BoundedChannelOptions(64)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                });

            void OnProgress(object? sender, DownloadJob job) => channel.Writer.TryWrite(job);
            downloads.Progress += OnProgress;
            try
            {
                // Send what is already in flight, so a page opened mid-download is not blank until the next tick.
                foreach (var job in await downloads.ListAsync(ct))
                {
                    if (job.State is DownloadState.Downloading or DownloadState.Queued)
                    {
                        await WriteAsync(http, job, ct);
                    }
                }

                await foreach (var job in channel.Reader.ReadAllAsync(ct))
                {
                    await WriteAsync(http, job, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // The browser navigated away or closed the page.
            }
            finally
            {
                downloads.Progress -= OnProgress;
            }

            return Results.Empty;
        }).WithName("NetCoreAI.Downloads.Events").ExcludeFromDescription();

        api.MapPost("/downloads", async (DownloadApiRequest request, IDownloadManager downloads, CancellationToken ct) =>
        {
            if (request.Files is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "Pick at least one file to download." });
            }

            var job = await downloads.EnqueueAsync(
                new DownloadRequest(request.Source ?? HuggingFaceClient.SourceId, request.RepoId, request.Files, request.Revision)
                {
                    ModelName = request.Name,
                    Tags = request.Tags,
                },
                ct);

            return Results.Created($"downloads/{job.Id}", job);
        }).WithName("NetCoreAI.Downloads.Create");

        api.MapPost("/downloads/{id}/pause", async (string id, IDownloadManager downloads, CancellationToken ct) =>
        {
            await downloads.PauseAsync(id, ct);
            return await downloads.GetAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound();
        }).WithName("NetCoreAI.Downloads.Pause");

        api.MapPost("/downloads/{id}/resume", async (string id, IDownloadManager downloads, CancellationToken ct) =>
        {
            await downloads.ResumeAsync(id, ct);
            return await downloads.GetAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound();
        }).WithName("NetCoreAI.Downloads.Resume");

        api.MapDelete("/downloads/{id}", async (string id, IDownloadManager downloads, CancellationToken ct) =>
        {
            await downloads.CancelAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Downloads.Cancel");
    }

    private static async Task WriteAsync(HttpContext http, DownloadJob job, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: progress\ndata: {System.Text.Json.JsonSerializer.Serialize(job, SseJson)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private static readonly System.Text.Json.JsonSerializerOptions SseJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>A download the dashboard asks for: a repository and the files of one variant.</summary>
    public sealed record DownloadApiRequest(string RepoId, IReadOnlyList<string> Files)
    {
        public string? Source { get; init; }

        public string? Revision { get; init; }

        /// <summary>Name to register the finished model under; defaults to the repository name.</summary>
        public string? Name { get; init; }

        public IReadOnlyList<string>? Tags { get; init; }
    }
}
