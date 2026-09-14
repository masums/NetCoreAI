using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>
/// Hugging Face as a model source: search, repository detail with file sizes and LFS hashes, README, and
/// resolved download URLs. Calls go through the server (never the browser), so the token stays server-side
/// and the response cache is shared by every user of the dashboard.
/// </summary>
public sealed class HuggingFaceClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<HuggingFaceClient> logger) : IModelSource
{
    public const string SourceId = "huggingface";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Id => SourceId;

    public string DisplayName => "Hugging Face";

    private string Endpoint => options.CurrentValue.Network.HuggingFaceEndpoint.TrimEnd('/');

    public async Task<IReadOnlyList<HubModelSummary>> SearchAsync(ModelSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var url = $"{Endpoint}/api/models?{BuildSearchQuery(query)}";
        var models = await GetAsync<List<HfModel>>(url, "the search", cancellationToken).ConfigureAwait(false) ?? [];

        var results = new List<HubModelSummary>(models.Count);
        foreach (var model in models)
        {
            var summary = ToSummary(model);

            // The search API reports no file list, so size filtering happens on what the tags imply.
            if (query.Format is { } format && summary.Formats.Count > 0 && !summary.Formats.Contains(format))
            {
                continue;
            }

            results.Add(summary);
        }

        return results;
    }

    /// <summary>Builds the query string; filters the API understands are sent, the rest are applied locally.</summary>
    internal static string BuildSearchQuery(ModelSearchQuery query)
    {
        var parts = new List<string>();
        if (query.Text is { Length: > 0 } text)
        {
            parts.Add($"search={Uri.EscapeDataString(text)}");
        }

        if (query.Author is { Length: > 0 } author)
        {
            parts.Add($"author={Uri.EscapeDataString(author)}");
        }

        // "filter" takes repeated tag values: the pipeline tag, the format library and the license.
        foreach (var filter in Filters(query))
        {
            parts.Add($"filter={Uri.EscapeDataString(filter)}");
        }

        parts.Add($"sort={Sort(query.Sort)}");
        parts.Add("direction=-1");
        parts.Add($"limit={Math.Clamp(query.Limit, 1, 100).ToString(CultureInfo.InvariantCulture)}");
        if (query.Offset > 0)
        {
            parts.Add($"skip={query.Offset.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join('&', parts);
    }

    private static IEnumerable<string> Filters(ModelSearchQuery query)
    {
        if (query.Task is { Length: > 0 } task)
        {
            yield return task;
        }

        if (query.Format is { } format && FormatTag(format) is { } tag)
        {
            yield return tag;
        }

        if (query.License is { Length: > 0 } license)
        {
            yield return license.StartsWith("license:", StringComparison.OrdinalIgnoreCase) ? license : $"license:{license}";
        }
    }

    private static string? FormatTag(ModelFormat format) => format switch
    {
        ModelFormat.Gguf => "gguf",
        ModelFormat.Onnx => "onnx",
        ModelFormat.Safetensors => "safetensors",
        _ => null,
    };

    private static string Sort(ModelSearchSort sort) => sort switch
    {
        ModelSearchSort.Likes => "likes",
        ModelSearchSort.LastModified => "lastModified",
        ModelSearchSort.Trending => "trendingScore",
        _ => "downloads",
    };

    public async Task<HubModelDetail?> GetAsync(string repoId, string? revision = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        var reference = revision is { Length: > 0 } ? revision : "main";
        var model = await GetAsync<HfModel>($"{Endpoint}/api/models/{repoId}?revision={Uri.EscapeDataString(reference)}", repoId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return null;
        }

        var paths = model.Siblings?.Select(s => s.Rfilename).Where(p => p is { Length: > 0 }).Select(p => p!).ToList() ?? [];
        var info = await GetPathsInfoAsync(repoId, reference, paths, cancellationToken).ConfigureAwait(false);

        var files = new List<HubFile>(paths.Count);
        foreach (var path in paths)
        {
            info.TryGetValue(path, out var detail);
            files.Add(new HubFile(
                path,
                detail?.Lfs?.Size ?? detail?.Size,
                // For LFS-stored weights the oid is the SHA-256, which is exactly what the downloader verifies.
                detail?.Lfs?.Oid,
                HubFormats.DetectFormat(path),
                HubFormats.DetectQuantization(path)));
        }

        var readme = await GetReadmeAsync(repoId, reference, cancellationToken).ConfigureAwait(false);
        return new HubModelDetail(ToSummary(model), readme, files, BuildMetadata(model));
    }

    private static Dictionary<string, string> BuildMetadata(HfModel model)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (model.Sha is { Length: > 0 } sha)
        {
            metadata["revision"] = sha;
        }

        if (model.PipelineTag is { Length: > 0 } pipeline)
        {
            metadata["pipeline_tag"] = pipeline;
        }

        if (model.LibraryName is { Length: > 0 } library)
        {
            metadata["library"] = library;
        }

        if (model.Tags is { Count: > 0 } tags)
        {
            metadata["tags"] = string.Join(',', tags);
        }

        return metadata;
    }

    /// <summary>File sizes and LFS hashes, which the model endpoint does not include.</summary>
    private async Task<IReadOnlyDictionary<string, HfPathInfo>> GetPathsInfoAsync(string repoId, string revision, List<string> paths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HfPathInfo>(StringComparer.Ordinal);
        if (paths.Count == 0)
        {
            return result;
        }

        // The endpoint takes a batch of paths; keep batches modest so a big repository stays one round trip each.
        const int batchSize = 200;
        for (var offset = 0; offset < paths.Count; offset += batchSize)
        {
            var batch = paths.Skip(offset).Take(batchSize).ToList();
            try
            {
                using var client = CreateClient();
                using var response = await client.PostAsJsonAsync(
                    $"{Endpoint}/api/models/{repoId}/paths-info/{Uri.EscapeDataString(revision)}",
                    new { paths = batch },
                    Json,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogDebug("Hugging Face paths-info for {RepoId} returned {Status}; sizes will be unknown.", repoId, response.StatusCode);
                    continue;
                }

                var infos = await response.Content.ReadFromJsonAsync<List<HfPathInfo>>(Json, cancellationToken).ConfigureAwait(false) ?? [];
                foreach (var info in infos.Where(i => i.Path is { Length: > 0 }))
                {
                    result[info.Path!] = info;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Sizes are a nicety: a repository still lists and downloads without them.
                logger.LogDebug(ex, "Could not read file sizes for {RepoId}.", repoId);
            }
        }

        return result;
    }

    private async Task<string?> GetReadmeAsync(string repoId, string revision, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(
                new Uri($"{Endpoint}/{repoId}/resolve/{Uri.EscapeDataString(revision)}/README.md"),
                cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Could not read the README of {RepoId}.", repoId);
            return null;
        }
    }

    public Task<HubDownloadLocation> GetDownloadLocationAsync(string repoId, string filePath, string? revision = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var reference = revision is { Length: > 0 } ? revision : "main";
        var url = new Uri($"{Endpoint}/{repoId}/resolve/{Uri.EscapeDataString(reference)}/{string.Join('/', filePath.Split('/').Select(Uri.EscapeDataString))}");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Token() is { Length: > 0 } token)
        {
            headers["Authorization"] = $"Bearer {token}";
        }

        return Task.FromResult(new HubDownloadLocation(url, headers, null, null));
    }

    /// <summary>The token for gated repositories: the environment wins over configuration, as in containers.</summary>
    private string? Token() =>
        Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } fromEnv
            ? fromEnv
            : options.CurrentValue.Network.HuggingFaceToken;

    private async Task<T?> GetAsync<T>(string url, string what, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        try
        {
            using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                return default;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Hugging Face answers 401 for a repository that does not exist as well as for one that is
                // private or gated, so that it never confirms a private repo exists. Both causes are named
                // here rather than guessing, because a typo in the id is the commonest of the three.
                throw new NetCoreAIException(Token() is { Length: > 0 }
                    ? $"Hugging Face refused access to '{what}'. Either it does not exist (check the id), or the configured token does not cover it — gated repositories also need their licence accepted on the site."
                    : $"Hugging Face refused access to '{what}'. Either it does not exist (check the id), or it is gated or private: accept its licence on the site and set a token in Settings → Network (or the HF_TOKEN environment variable).");
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                throw new NetCoreAIException("Hugging Face is rate-limiting this host. Wait a moment, or set a token in Settings → Network to raise the limit.");
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NetCoreAIException($"Could not reach Hugging Face at {Endpoint}: {ex.Message}", ex);
        }
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(NetCoreAIHttp.HubClient);
        if (Token() is { Length: > 0 } token)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static HubModelSummary ToSummary(HfModel model)
    {
        var tags = model.Tags ?? [];
        var formats = new List<ModelFormat>();
        foreach (var (tag, format) in FormatTags)
        {
            if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                formats.Add(format);
            }
        }

        var id = model.Id ?? model.ModelId ?? string.Empty;
        return new HubModelSummary(
            SourceId,
            id,
            id.Contains('/', StringComparison.Ordinal) ? id[(id.IndexOf('/', StringComparison.Ordinal) + 1)..] : id,
            model.Author ?? (id.Contains('/', StringComparison.Ordinal) ? id[..id.IndexOf('/', StringComparison.Ordinal)] : null),
            formats,
            model.Downloads,
            model.Likes,
            model.LastModified,
            License(model),
            model.Gated.ValueKind is JsonValueKind.String or JsonValueKind.True,
            model.PipelineTag);
    }

    private static readonly (string Tag, ModelFormat Format)[] FormatTags =
    [
        ("gguf", ModelFormat.Gguf),
        ("onnx", ModelFormat.Onnx),
        ("safetensors", ModelFormat.Safetensors),
    ];

    /// <summary>The licence, from the card when it is there and otherwise from the "license:" tag.</summary>
    private static string? License(HfModel model)
    {
        if (model.CardData is { } card && card.TryGetProperty("license", out var license) && license.ValueKind == JsonValueKind.String)
        {
            return license.GetString();
        }

        return model.Tags?.FirstOrDefault(t => t.StartsWith("license:", StringComparison.OrdinalIgnoreCase))?["license:".Length..];
    }

    private sealed record HfModel
    {
        public string? Id { get; init; }
        public string? ModelId { get; init; }
        public string? Author { get; init; }
        public string? Sha { get; init; }
        public long Downloads { get; init; }
        public long Likes { get; init; }
        public DateTimeOffset? LastModified { get; init; }
        public string? PipelineTag { get; init; }
        public string? LibraryName { get; init; }
        public List<string>? Tags { get; init; }

        /// <summary>False, or the gating kind ("auto", "manual") when the repo is gated.</summary>
        public JsonElement Gated { get; init; }

        public JsonElement? CardData { get; init; }
        public List<HfSibling>? Siblings { get; init; }
    }

    private sealed record HfSibling
    {
        [JsonPropertyName("rfilename")]
        public string? Rfilename { get; init; }
    }

    private sealed record HfPathInfo
    {
        public string? Path { get; init; }
        public long? Size { get; init; }
        public HfLfs? Lfs { get; init; }
    }

    private sealed record HfLfs
    {
        /// <summary>SHA-256 of the file contents.</summary>
        public string? Oid { get; init; }

        public long? Size { get; init; }
    }
}
