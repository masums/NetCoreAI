namespace NetCoreAI;

/// <summary>Search filters for a model hub.</summary>
public sealed record ModelSearchQuery
{
    public string? Text { get; init; }
    public string? Author { get; init; }
    /// <summary>Hub task tag, e.g. "text-generation", "feature-extraction".</summary>
    public string? Task { get; init; }
    public ModelFormat? Format { get; init; }
    public string? License { get; init; }
    public long? MaxSizeBytes { get; init; }
    public ModelSearchSort Sort { get; init; } = ModelSearchSort.Downloads;
    public int Limit { get; init; } = 30;
    public int Offset { get; init; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ModelSearchSort>))]
public enum ModelSearchSort
{
    Downloads,
    Likes,
    LastModified,
    Trending,
}

/// <summary>A row in hub search results.</summary>
public sealed record HubModelSummary(string SourceId, string RepoId, string Name, string? Author, IReadOnlyList<ModelFormat> Formats, long Downloads, long Likes, DateTimeOffset? LastModified, string? License, bool Gated, string? PipelineTag);

/// <summary>A downloadable file in a hub repository.</summary>
public sealed record HubFile(string Path, long? SizeBytes, string? Sha256, ModelFormat? Format, string? Quantization)
{
    public bool IsModelWeights => Format is not null;
}

/// <summary>Full detail of a hub repository.</summary>
public sealed record HubModelDetail(HubModelSummary Summary, string? ReadmeMarkdown, IReadOnlyList<HubFile> Files, IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>Set by the core after asking the fit estimator, per weights file.</summary>
    public IReadOnlyDictionary<string, MemoryEstimate> FitByFile { get; init; } = new Dictionary<string, MemoryEstimate>();
}

/// <summary>A browsable/downloadable source of models (Hugging Face by default; Ollama library, ModelScope, private registries later).</summary>
public interface IModelSource
{
    string Id { get; }

    string DisplayName { get; }

    Task<IReadOnlyList<HubModelSummary>> SearchAsync(ModelSearchQuery query, CancellationToken cancellationToken = default);

    Task<HubModelDetail?> GetAsync(string repoId, string? revision = null, CancellationToken cancellationToken = default);

    /// <summary>Direct download URL for a file, including any auth headers the downloader must send.</summary>
    Task<HubDownloadLocation> GetDownloadLocationAsync(string repoId, string filePath, string? revision = null, CancellationToken cancellationToken = default);
}

public sealed record HubDownloadLocation(Uri Url, IReadOnlyDictionary<string, string> Headers, long? SizeBytes, string? Sha256);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DownloadState>))]
public enum DownloadState
{
    Queued,
    Downloading,
    Paused,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Request to download one or more files from a hub repository into the data directory.</summary>
public sealed record DownloadRequest(string SourceId, string RepoId, IReadOnlyList<string> Files, string? Revision = null, int Priority = 0)
{
    /// <summary>Register the result in the model registry with this name (defaults to repo id).</summary>
    public string? ModelName { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>Persisted, resumable download job.</summary>
public sealed record DownloadJob
{
    public required string Id { get; init; }
    public required DownloadRequest Request { get; init; }
    public DownloadState State { get; init; } = DownloadState.Queued;
    public long BytesTotal { get; init; }
    public long BytesDone { get; init; }
    public double BytesPerSecond { get; init; }
    public string? CurrentFile { get; init; }
    public string? Error { get; init; }
    public string? ResultModelId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public double Progress => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

/// <summary>Queue of hub downloads with progress, persisted so it survives host restarts.</summary>
public interface IDownloadManager
{
    Task<DownloadJob> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadJob>> ListAsync(CancellationToken cancellationToken = default);
    Task<DownloadJob?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task PauseAsync(string id, CancellationToken cancellationToken = default);
    Task ResumeAsync(string id, CancellationToken cancellationToken = default);
    Task CancelAsync(string id, CancellationToken cancellationToken = default);
    event EventHandler<DownloadJob>? Progress;
}

/// <summary>Imports a model that already exists on disk or at a URL, detecting format and metadata.</summary>
public interface IModelImporter
{
    Task<ModelDescriptor> ImportFromPathAsync(string path, string? name = null, bool copyIntoDataDirectory = false, CancellationToken cancellationToken = default);
    Task<DownloadJob> ImportFromUrlAsync(Uri url, string? name = null, CancellationToken cancellationToken = default);
}
