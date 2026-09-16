using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Storage;

/// <summary>Disk used by one registered model.</summary>
/// <param name="ModelId">Registry id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Path">File or folder holding the weights, absolute.</param>
/// <param name="SizeBytes">Bytes on disk; 0 when the files are missing.</param>
/// <param name="Exists">False when the registry points at something that is no longer there.</param>
public sealed record ModelStorageUsage(string ModelId, string Name, string? Path, long SizeBytes, bool Exists)
{
    public DateTimeOffset? LastUsedAt { get; init; }

    public ModelStatus Status { get; init; }
}

/// <summary>A file or folder in the data directory that no registered model claims.</summary>
/// <param name="Path">Absolute path.</param>
/// <param name="SizeBytes">Bytes it occupies.</param>
/// <param name="ModifiedAt">Last write time, to tell a stale download from something in progress.</param>
/// <param name="Reason">Why it looks orphaned, in words a user can act on.</param>
public sealed record OrphanedFile(string Path, long SizeBytes, DateTimeOffset ModifiedAt, string Reason);

/// <summary>What the data directory holds and how much room is left.</summary>
public sealed record StorageUsage
{
    public required string DataDirectory { get; init; }

    /// <summary>Bytes under the data directory, models and everything else.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Bytes accounted for by registered models.</summary>
    public long ModelBytes { get; init; }

    /// <summary>Bytes in files no model claims, including interrupted downloads.</summary>
    public long OrphanBytes { get; init; }

    /// <summary>Free space on the volume holding the data directory; null when it cannot be read.</summary>
    public long? FreeDiskBytes { get; init; }

    /// <summary>Configured warning threshold, from <see cref="ModelsOptions.StorageQuotaWarningBytes"/>.</summary>
    public long? QuotaWarningBytes { get; init; }

    /// <summary>True when usage has passed the configured threshold.</summary>
    public bool OverQuota => QuotaWarningBytes is { } quota && TotalBytes > quota;

    /// <summary>True when the volume has less free space than the data directory already uses.</summary>
    public bool LowDisk => FreeDiskBytes is { } free && free < Math.Max(1_073_741_824, TotalBytes / 10);

    public IReadOnlyList<ModelStorageUsage> Models { get; init; } = [];
}

/// <summary>Disk accounting for the data directory: what each model costs, and what is left behind.</summary>
public interface IStorageService
{
    Task<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>Files under the data directory that no registered model claims.</summary>
    Task<IReadOnlyList<OrphanedFile>> ScanOrphansAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes orphaned paths. Anything a model claims is refused, so a stale list cannot delete weights.</summary>
    Task<long> DeleteOrphansAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

internal sealed class StorageService(
    IModelRegistry registry,
    IEnumerable<IModelFormatDetector> detectors,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<StorageService> logger) : IStorageService
{
    private readonly List<IModelFormatDetector> _detectors = [.. detectors];

    /// <summary>Weight files, as opposed to the configuration and vocabulary that sit beside them.</summary>
    private static readonly string[] WeightExtensions = [".gguf", ".onnx", ".safetensors", ".bin", ".pt", ".pth"];

    /// <summary>Subfolders that belong to NetCoreAI itself and are never reported as orphans.</summary>
    private static readonly string[] ReservedFolders = ["vectors", "keys", "uploads", "logs"];

    private string DataDirectory => Path.GetFullPath(options.CurrentValue.DataDirectory);

    public async Task<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var root = DataDirectory;
        var models = new List<ModelStorageUsage>();
        long modelBytes = 0;

        foreach (var entry in await registry.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = entry.Descriptor;
            if (descriptor.IsRemote)
            {
                // A remote model costs nothing on this disk.
                continue;
            }

            var path = Resolve(descriptor.Path);
            var size = path is null ? 0 : Measure(path);
            modelBytes += size;
            models.Add(new ModelStorageUsage(descriptor.Id, descriptor.Name, path, size, path is not null && Exists(path))
            {
                LastUsedAt = descriptor.LastUsedAt,
                Status = entry.Status,
            });
        }

        var total = Directory.Exists(root) ? Measure(root) : 0;
        var orphans = await ScanOrphansAsync(cancellationToken).ConfigureAwait(false);

        return new StorageUsage
        {
            DataDirectory = root,
            TotalBytes = total,
            ModelBytes = modelBytes,
            OrphanBytes = orphans.Sum(o => o.SizeBytes),
            FreeDiskBytes = FreeSpace(root),
            QuotaWarningBytes = options.CurrentValue.Models.StorageQuotaWarningBytes,
            Models = [.. models.OrderByDescending(m => m.SizeBytes)],
        };
    }

    public async Task<IReadOnlyList<OrphanedFile>> ScanOrphansAsync(CancellationToken cancellationToken = default)
    {
        var root = DataDirectory;
        var modelsRoot = Path.Combine(root, "models");
        if (!Directory.Exists(modelsRoot))
        {
            return [];
        }

        var claimed = await ClaimedPathsAsync(cancellationToken).ConfigureAwait(false);
        var orphans = new List<OrphanedFile>();

        // Only the models folder is scanned: the metadata database, vector store and keyring live elsewhere
        // under the data directory and are never this scan's business.
        foreach (var path in Directory.EnumerateFileSystemEntries(modelsRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path) || IsReserved(path, root))
            {
                continue;
            }

            var isPartial = path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".part.json", StringComparison.OrdinalIgnoreCase);

            if (!isPartial && IsClaimed(path, claimed))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                orphans.Add(new OrphanedFile(
                    path,
                    info.Length,
                    info.LastWriteTimeUtc,
                    isPartial
                        ? "An interrupted download. Resume it from the Downloads panel, or delete it to reclaim the space."
                        : Reason(path, modelsRoot)));
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not stat {Path} while scanning for orphans.", path);
            }
        }

        return [.. orphans.OrderByDescending(o => o.SizeBytes)];
    }

    /// <summary>
    /// Why a file is unclaimed — which is not always "somebody left it behind".
    /// </summary>
    /// <remarks>
    /// A model whose format no installed backend can read is unclaimed for a completely different reason:
    /// it was downloaded, it is intact, and nothing in the host can open it. Telling somebody that is
    /// left over from a removed model invites them to delete a model they still want, and the fix is a
    /// package reference rather than a deletion. Found after an ONNX folder was listed as reclaimable on
    /// a host with no ONNX backend registered.
    /// </remarks>
    private string Reason(string path, string modelsRoot)
    {
        var folder = ModelFolder(path, modelsRoot);

        // Recognised by something installed, and still unclaimed: genuinely left behind.
        if (_detectors.Exists(d => d.TryDetect(path) is not null)
            || (folder is not null && _detectors.Exists(d => d.TryDetect(folder) is not null)))
        {
            return "No registered model claims this file. It is left over from a removed model or a manual copy.";
        }

        if (HasWeights(path, folder))
        {
            return "No installed backend can read this model's format, which is why nothing claims it. "
                + "Add the matching backend package — NetCoreAI.Backend.Gguf for .gguf files, "
                + "NetCoreAI.Backend.Onnx for ONNX folders — and import it, rather than deleting it.";
        }

        return "No registered model claims this file. It is left over from a removed model or a manual copy.";
    }

    /// <summary>The first folder under <c>models/</c> that <paramref name="path"/> sits in, if any.</summary>
    /// <remarks>
    /// A model in a folder is a set of files: weights, a tokenizer, a config. They are unclaimed or
    /// claimed together, so the reason is worked out for the folder and given to every file in it —
    /// otherwise the weights would explain themselves and their companions would not.
    /// </remarks>
    private static string? ModelFolder(string path, string modelsRoot)
    {
        var directory = Path.GetDirectoryName(path);
        string? candidate = null;

        while (directory is not null
            && directory.StartsWith(modelsRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), modelsRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            candidate = directory;
            directory = Path.GetDirectoryName(directory);
        }

        return candidate;
    }

    private static bool HasWeights(string path, string? folder)
    {
        if (WeightExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (folder is null)
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(f => WeightExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<long> DeleteOrphansAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var root = DataDirectory;
        var claimed = await ClaimedPathsAsync(cancellationToken).ConfigureAwait(false);
        long freed = 0;

        foreach (var raw in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(raw);

            // Two guards, because this deletes files: stay inside the data directory, and never touch a
            // path a model claims even if the caller's list is stale.
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new NetCoreAIException($"{path} is outside the data directory, so it was not deleted.");
            }

            var isPartial = path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".part.json", StringComparison.OrdinalIgnoreCase);
            if (!isPartial && IsClaimed(path, claimed))
            {
                throw new NetCoreAIException($"{path} belongs to a registered model, so it was not deleted. Remove the model instead.");
            }

            try
            {
                if (File.Exists(path))
                {
                    freed += new FileInfo(path).Length;
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete {Path}.", path);
            }
        }

        if (freed > 0)
        {
            logger.LogInformation("Deleted orphaned files, reclaiming {Bytes} bytes.", freed);
        }

        return freed;
    }

    /// <summary>Paths registered models occupy, as absolute strings for prefix matching.</summary>
    private async Task<List<string>> ClaimedPathsAsync(CancellationToken cancellationToken)
    {
        var claimed = new List<string>();
        foreach (var entry in await registry.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Resolve(entry.Descriptor.Path) is { } path)
            {
                claimed.Add(path);
            }
        }

        return claimed;
    }

    /// <summary>True when the path is a claimed file, or sits inside a claimed folder (an ONNX export).</summary>
    private static bool IsClaimed(string path, List<string> claimed) =>
        claimed.Any(c => path.Equals(c, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static bool IsReserved(string path, string root) =>
        ReservedFolders.Any(f => path.StartsWith(Path.Combine(root, f) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private string? Resolve(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return null;
        }

        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(DataDirectory, path));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Bytes a file or folder occupies; 0 when it is gone or unreadable.</summary>
    private static long Measure(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (!Directory.Exists(path))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long? FreeSpace(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path) ?? path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Network shares and some containers do not report free space; the UI shows "unknown".
            return null;
        }
    }
}
