using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>
/// Brings a model that already exists into the registry: a file or folder on disk, or a direct URL.
/// The format is identified by asking the registered backends, so Core stays free of weight-format code
/// and an air-gapped install can register models with no network at all.
/// </summary>
internal sealed class ModelImporter(
    IEnumerable<IModelFormatDetector> detectors,
    IModelRegistry registry,
    IDownloadManager downloads,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<ModelImporter> logger) : IModelImporter
{
    private readonly List<IModelFormatDetector> _detectors = [.. detectors];

    public async Task<ModelDescriptor> ImportFromPathAsync(string path, string? name = null, bool copyIntoDataDirectory = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new NetCoreAIException($"Nothing exists at {full}. Check the path, and remember it is read by the server rather than the browser.");
        }

        var detected = Detect(full)
            ?? throw new NetCoreAIException(
                $"No registered backend recognised {full}. Add NetCoreAI.Backend.Gguf for .gguf files or NetCoreAI.Backend.Onnx for ONNX folders, then import again.");

        var finalPath = copyIntoDataDirectory
            ? await CopyAsync(full, cancellationToken).ConfigureAwait(false)
            : full;

        var displayName = name ?? detected.Name ?? Path.GetFileNameWithoutExtension(full.TrimEnd(Path.DirectorySeparatorChar));
        var descriptor = new ModelDescriptor
        {
            Id = DownloadManager.Slug("import", displayName),
            Name = displayName,
            Format = detected.Format,
            ProviderId = detected.ProviderId,
            Path = finalPath,
            SizeBytes = detected.SizeBytes,
            Quantization = detected.Quantization ?? HubFormats.DetectQuantization(full),
            ContextLength = detected.ContextLength,
            Family = detected.Family,
            ParameterCount = detected.ParameterCount,
            Capabilities = detected.Capabilities,
            Source = $"import:{full}",
        };

        logger.LogInformation("Importing {Format} model {Name} from {Path}.", detected.Format, displayName, finalPath);
        return await registry.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    public Task<DownloadJob> ImportFromUrlAsync(Uri url, string? name = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Scheme is not ("http" or "https"))
        {
            throw new NetCoreAIException($"Only http and https URLs can be imported; {url.Scheme} is not supported.");
        }

        // A bare URL is downloaded through the same queue as a hub file, so it resumes and verifies alike.
        var file = Path.GetFileName(url.LocalPath);
        if (string.IsNullOrWhiteSpace(file))
        {
            throw new NetCoreAIException($"{url} does not end in a file name, so there is nothing to save. Link directly to the model file.");
        }

        return downloads.EnqueueAsync(
            new DownloadRequest(UrlModelSource.SourceId, url.GetLeftPart(UriPartial.Path)[..^file.Length].TrimEnd('/'), [file])
            {
                ModelName = name ?? Path.GetFileNameWithoutExtension(file),
            },
            cancellationToken);
    }

    /// <summary>Detects by asking each backend in turn; the first that recognises the path wins.</summary>
    internal DetectedModel? Detect(string path)
    {
        foreach (var detector in _detectors)
        {
            try
            {
                if (detector.TryDetect(path) is { } detected)
                {
                    return detected;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A backend that throws on an unfamiliar file must not stop the others from looking.
                logger.LogDebug(ex, "Detector {Detector} failed on {Path}.", detector.GetType().Name, path);
            }
        }

        return null;
    }

    /// <summary>Copies an imported model into the data directory, so deleting it later stays self-contained.</summary>
    private async Task<string> CopyAsync(string source, CancellationToken cancellationToken)
    {
        var root = Path.Combine(options.CurrentValue.DataDirectory, "models", "imported");
        Directory.CreateDirectory(root);

        if (File.Exists(source))
        {
            var destination = Path.Combine(root, Path.GetFileName(source));
            await CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
            return destination;
        }

        var folder = Path.Combine(root, new DirectoryInfo(source).Name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyFileAsync(file, destination, cancellationToken).ConfigureAwait(false);
        }

        return folder;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        await using var to = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Treats a plain URL as a one-file "repository" so URL import can reuse the download queue, with its
/// resume, rate limiting and hash verification, instead of having a second download path.
/// </summary>
internal sealed class UrlModelSource : IModelSource
{
    public const string SourceId = "url";

    public string Id => SourceId;

    public string DisplayName => "Direct URL";

    public Task<IReadOnlyList<HubModelSummary>> SearchAsync(ModelSearchQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HubModelSummary>>([]);

    public Task<HubModelDetail?> GetAsync(string repoId, string? revision = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<HubModelDetail?>(null);

    public Task<HubDownloadLocation> GetDownloadLocationAsync(string repoId, string filePath, string? revision = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // The "repo id" is the URL up to the file name, so the two halves rejoin here.
        return Task.FromResult(new HubDownloadLocation(
            new Uri($"{repoId.TrimEnd('/')}/{filePath}"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            null,
            null));
    }
}
