using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>
/// The download queue: one job per requested variant, run one at a time so a slow network is not split
/// between files, with per-file resume, verification and progress that survives a host restart.
/// </summary>
/// <remarks>
/// Jobs are persisted as they change, so a restart re-queues whatever was in flight and the partially
/// transferred bytes on disk are picked up rather than fetched again. A completed job registers its result
/// in the model registry, which is what makes a downloaded model appear on the Models page.
/// </remarks>
internal sealed class DownloadManager(
    IEnumerable<IModelSource> sources,
    FileDownloader downloader,
    IMetadataStore store,
    IModelRegistry registry,
    IEnumerable<IModelFormatDetector> detectors,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<DownloadManager> logger) : IDownloadManager, IHostedService, IDisposable
{
    private readonly List<IModelSource> _sources = [.. sources];
    private readonly List<IModelFormatDetector> _detectors = [.. detectors];
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _paused = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? _stopping;
    private Task? _worker;

    public event EventHandler<DownloadJob>? Progress;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var job in await store.Downloads.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            _jobs[job.Id] = job;

            // Anything that was mid-flight when the host stopped goes back in the queue; the bytes already
            // on disk are reused, so this is a resume rather than a restart.
            if (job.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying)
            {
                await UpdateAsync(job with { State = DownloadState.Queued, BytesPerSecond = 0 }, cancellationToken).ConfigureAwait(false);
                await _queue.Writer.WriteAsync(job.Id, cancellationToken).ConfigureAwait(false);
            }
            else if (job.State == DownloadState.Paused)
            {
                _paused[job.Id] = true;
            }
        }

        _stopping = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        if (_stopping is { } stopping)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        foreach (var cts in _running.Values)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (_worker is { } worker)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // The part files on disk carry the progress, so a hard stop costs nothing but the current chunk.
            }
        }
    }

    public async Task<DownloadJob> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Files.Count == 0)
        {
            throw new NetCoreAIException("A download needs at least one file. Pick a variant on the model page.");
        }

        if (options.CurrentValue.Network.OfflineMode)
        {
            throw new OfflineModeException("Offline mode is on, so nothing can be downloaded. Turn it off in Settings → Network, or import the model from disk.");
        }

        // Asking for the same thing twice is a double click, not a second download.
        if (_jobs.Values.FirstOrDefault(j => Duplicate(j, request)) is { } existing)
        {
            logger.LogDebug("Download of {RepoId} is already {State}; reusing job {JobId}.", request.RepoId, existing.State, existing.Id);
            return existing;
        }

        var job = new DownloadJob
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Request = request,
            State = DownloadState.Queued,
        };

        await UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(job.Id, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Queued download {JobId}: {RepoId} ({FileCount} files).", job.Id, request.RepoId, request.Files.Count);
        return job;
    }

    private static bool Duplicate(DownloadJob job, DownloadRequest request) =>
        job.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused
        && job.Request.RepoId.Equals(request.RepoId, StringComparison.OrdinalIgnoreCase)
        && job.Request.Files.Count == request.Files.Count
        && job.Request.Files.OrderBy(f => f, StringComparer.Ordinal).SequenceEqual(request.Files.OrderBy(f => f, StringComparer.Ordinal), StringComparer.Ordinal);

    public Task<IReadOnlyList<DownloadJob>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DownloadJob>>([.. _jobs.Values.OrderByDescending(j => j.CreatedAt)]);

    public Task<DownloadJob?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

    public async Task PauseAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(id, out var job) && job.State is DownloadState.Queued or DownloadState.Downloading)
        {
            _paused[id] = true;
            if (_running.TryGetValue(id, out var cts))
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }

            await UpdateAsync(job with { State = DownloadState.Paused, BytesPerSecond = 0 }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResumeAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(id, out var job) && job.State is DownloadState.Paused or DownloadState.Failed)
        {
            _paused.TryRemove(id, out _);
            await UpdateAsync(job with { State = DownloadState.Queued, Error = null }, cancellationToken).ConfigureAwait(false);
            await _queue.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }

        _paused.TryRemove(id, out _);
        if (_running.TryGetValue(id, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        await UpdateAsync(job with { State = DownloadState.Cancelled, BytesPerSecond = 0, CompletedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);

        // Partial bytes for a cancelled job are dead weight in the data directory.
        DeletePartials(job);
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                if (!_jobs.TryGetValue(id, out var job) || job.State is DownloadState.Cancelled or DownloadState.Completed || _paused.ContainsKey(id))
                {
                    continue;
                }

                await RunJobAsync(job, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            // The worker must never die silently: without it the queue would stall for the process lifetime.
            logger.LogError(ex, "The download worker stopped unexpectedly. Restart the host to resume downloads.");
        }
    }

    private async Task RunJobAsync(DownloadJob job, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[job.Id] = cts;

        try
        {
            var source = _sources.FirstOrDefault(s => s.Id.Equals(job.Request.SourceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new NetCoreAIException($"No model source with id '{job.Request.SourceId}' is registered, so {job.Request.RepoId} cannot be downloaded.");

            var directory = TargetDirectory(job.Request);
            var locations = new List<(string File, HubDownloadLocation Location)>(job.Request.Files.Count);
            long total = 0;
            foreach (var file in job.Request.Files)
            {
                var location = await source.GetDownloadLocationAsync(job.Request.RepoId, file, job.Request.Revision, cts.Token).ConfigureAwait(false);
                locations.Add((file, location));
                total += location.SizeBytes ?? 0;
            }

            job = job with { State = DownloadState.Downloading, StartedAt = DateTimeOffset.UtcNow, BytesTotal = total > 0 ? total : job.BytesTotal };
            await UpdateAsync(job, cts.Token).ConfigureAwait(false);

            long completedBytes = 0;
            foreach (var (file, location) in locations)
            {
                cts.Token.ThrowIfCancellationRequested();
                var destination = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
                var current = job with { CurrentFile = file };

                if (File.Exists(destination))
                {
                    // Already fetched by an earlier run of this job.
                    completedBytes += new FileInfo(destination).Length;
                    continue;
                }

                var bytesBefore = completedBytes;
                await downloader.DownloadAsync(
                    location,
                    destination,
                    p => Report(current, bytesBefore + p.BytesDone, p.BytesPerSecond),
                    cts.Token).ConfigureAwait(false);

                completedBytes += File.Exists(destination) ? new FileInfo(destination).Length : 0;
                job = current with { BytesDone = completedBytes };
                await UpdateAsync(job, cts.Token).ConfigureAwait(false);
            }

            var model = await RegisterAsync(job, directory, cts.Token).ConfigureAwait(false);
            await UpdateAsync(job with
            {
                State = DownloadState.Completed,
                BytesDone = completedBytes,
                BytesTotal = completedBytes > job.BytesTotal ? completedBytes : job.BytesTotal,
                BytesPerSecond = 0,
                CurrentFile = null,
                ResultModelId = model?.Id,
                CompletedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);

            logger.LogInformation("Download {JobId} completed: {RepoId} → {ModelId}.", job.Id, job.Request.RepoId, model?.Id ?? "(not registered)");
        }
        catch (OperationCanceledException)
        {
            // Pause and cancel both cancel the token; their own state was already written.
            if (_jobs.TryGetValue(job.Id, out var current) && current.State == DownloadState.Downloading)
            {
                await UpdateAsync(current with { State = DownloadState.Paused, BytesPerSecond = 0 }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Download {JobId} of {RepoId} failed.", job.Id, job.Request.RepoId);
            await UpdateAsync(job with
            {
                State = DownloadState.Failed,
                BytesPerSecond = 0,
                Error = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
        }
    }

    /// <summary>Registers what was downloaded so it shows up on the Models page, ready to load.</summary>
    private async Task<ModelDescriptor?> RegisterAsync(DownloadJob job, string directory, CancellationToken cancellationToken)
    {
        var request = job.Request;
        var path = ModelPath(request, directory);
        if (path is null)
        {
            logger.LogWarning("Download {JobId} finished but held no model weights, so nothing was registered.", job.Id);
            return null;
        }

        var detected = _detectors.Select(d => d.TryDetect(path)).FirstOrDefault(d => d is not null);
        if (detected is null)
        {
            logger.LogWarning(
                "No backend recognised {Path}. The files are on disk; add the matching backend package (NetCoreAI.Backend.Gguf or .Onnx) and import the folder.",
                path);
            return null;
        }

        var name = request.ModelName ?? request.RepoId[(request.RepoId.IndexOf('/', StringComparison.Ordinal) + 1)..];
        var descriptor = new ModelDescriptor
        {
            Id = Slug(request.RepoId, Path.GetFileNameWithoutExtension(path)),
            Name = name,
            Format = detected.Format,
            ProviderId = detected.ProviderId,
            Path = path,
            SizeBytes = job.BytesDone > 0 ? job.BytesDone : null,
            Quantization = detected.Quantization ?? HubFormats.DetectQuantization(path),
            ContextLength = detected.ContextLength,
            Family = detected.Family,
            ParameterCount = detected.ParameterCount,
            Capabilities = detected.Capabilities,
            Source = $"{request.SourceId}:{request.RepoId}",
            Revision = request.Revision,
            Tags = request.Tags ?? [],
        };

        return await registry.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file or folder a provider should be pointed at: the GGUF file, or the ONNX folder.</summary>
    private static string? ModelPath(DownloadRequest request, string directory)
    {
        var weights = request.Files.FirstOrDefault(f => HubFormats.DetectFormat(f) == ModelFormat.Gguf);
        if (weights is not null)
        {
            return Path.Combine(directory, weights.Replace('/', Path.DirectorySeparatorChar));
        }

        var config = request.Files.FirstOrDefault(f => Path.GetFileName(f).Equals(HubFormats.GenAiConfigFile, StringComparison.OrdinalIgnoreCase))
            ?? request.Files.FirstOrDefault(f => HubFormats.DetectFormat(f) == ModelFormat.Onnx);

        return config is null
            ? null
            : Path.GetDirectoryName(Path.Combine(directory, config.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Files land under models/{owner}--{repo}, keeping the repository's own subfolders.</summary>
    private string TargetDirectory(DownloadRequest request) =>
        Path.Combine(options.CurrentValue.DataDirectory, "models", request.RepoId.Replace('/', '-').Replace('\\', '-'));

    internal static string Slug(string repoId, string variant)
    {
        var raw = $"{repoId.Replace('/', '-')}-{variant}".ToLowerInvariant();
        var slug = new string([.. raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Length > 96 ? slug[..96].Trim('-') : slug;
    }

    private void Report(DownloadJob job, long bytesDone, double bytesPerSecond)
    {
        var updated = job with { BytesDone = bytesDone, BytesPerSecond = bytesPerSecond };
        _jobs[job.Id] = updated;
        Progress?.Invoke(this, updated);
    }

    /// <summary>Writes a state change through to the store and tells listeners; progress ticks are not persisted.</summary>
    private async Task UpdateAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        _jobs[job.Id] = job;
        try
        {
            await store.Downloads.UpsertAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not persist download {JobId}; it will not resume after a restart.", job.Id);
        }

        Progress?.Invoke(this, job);
    }

    private void DeletePartials(DownloadJob job)
    {
        var directory = TargetDirectory(job.Request);
        foreach (var file in job.Request.Files)
        {
            foreach (var suffix in (string[])[".part", ".part.json"])
            {
                try
                {
                    var path = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar)) + suffix;
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public void Dispose()
    {
        _stopping?.Dispose();
        foreach (var cts in _running.Values)
        {
            cts.Dispose();
        }
    }
}
