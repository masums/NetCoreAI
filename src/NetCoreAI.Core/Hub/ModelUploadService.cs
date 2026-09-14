using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>An upload in progress.</summary>
/// <param name="UploadId">Server-generated id; the client sends this with every chunk.</param>
/// <param name="FileName">Sanitized file name the upload will land under.</param>
/// <param name="SizeBytes">Total size the client declared.</param>
/// <param name="ReceivedBytes">What has arrived so far, so an interrupted upload can resume.</param>
public sealed record UploadSession(string UploadId, string FileName, long SizeBytes, long ReceivedBytes)
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public double Progress => SizeBytes <= 0 ? 0 : Math.Clamp((double)ReceivedBytes / SizeBytes, 0, 1);
}

/// <summary>
/// Accepts a model file from the browser in chunks, so a multi-gigabyte GGUF can be uploaded without
/// holding it in memory or hitting a request size limit, and can resume from what already arrived.
/// </summary>
public interface IModelUploadService
{
    Task<UploadSession> BeginAsync(string fileName, long sizeBytes, CancellationToken cancellationToken = default);

    /// <summary>Appends one chunk at <paramref name="offset"/>. Out-of-order or duplicate chunks are rejected.</summary>
    Task<UploadSession> AppendAsync(string uploadId, long offset, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Finishes the upload, detects the format and registers the model.</summary>
    Task<ModelDescriptor> CompleteAsync(string uploadId, string? name = null, CancellationToken cancellationToken = default);

    Task AbortAsync(string uploadId, CancellationToken cancellationToken = default);

    Task<UploadSession?> GetAsync(string uploadId, CancellationToken cancellationToken = default);
}

internal sealed class ModelUploadService(
    IModelImporter importer,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<ModelUploadService> logger) : IModelUploadService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string UploadRoot => Path.Combine(options.CurrentValue.DataDirectory, "uploads");

    public Task<UploadSession> BeginAsync(string fileName, long sizeBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var safe = Sanitize(fileName);
        if (sizeBytes <= 0)
        {
            throw new NetCoreAIException("The upload declared no size. Pick the file again.");
        }

        var session = new UploadSession(Guid.NewGuid().ToString("N"), safe, sizeBytes, 0);
        var directory = DirectoryFor(session.UploadId);
        Directory.CreateDirectory(directory);
        Save(session);

        logger.LogInformation("Upload {UploadId} started for {FileName} ({Bytes} bytes).", session.UploadId, safe, sizeBytes);
        return Task.FromResult(session);
    }

    public async Task<UploadSession> AppendAsync(string uploadId, long offset, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var session = await GetAsync(uploadId, cancellationToken).ConfigureAwait(false)
            ?? throw new NetCoreAIException($"Upload '{uploadId}' is not in progress. It may have been completed or cleaned up; start the upload again.");

        if (offset != session.ReceivedBytes)
        {
            // Chunks are appended, so a gap or a repeat would silently corrupt the file.
            throw new NetCoreAIException(
                $"Chunk arrived at offset {offset} but the file has {session.ReceivedBytes} bytes. Send chunks in order, starting from where the last one ended.");
        }

        var path = FilePath(session);
        await using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var received = new FileInfo(path).Length;
        if (received > session.SizeBytes)
        {
            await AbortAsync(uploadId, cancellationToken).ConfigureAwait(false);
            throw new NetCoreAIException($"The upload sent more bytes than it declared ({received} of {session.SizeBytes}); it was discarded.");
        }

        var updated = session with { ReceivedBytes = received };
        Save(updated);
        return updated;
    }

    public async Task<ModelDescriptor> CompleteAsync(string uploadId, string? name = null, CancellationToken cancellationToken = default)
    {
        var session = await GetAsync(uploadId, cancellationToken).ConfigureAwait(false)
            ?? throw new NetCoreAIException($"Upload '{uploadId}' is not in progress.");

        var path = FilePath(session);
        var received = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (received != session.SizeBytes)
        {
            throw new NetCoreAIException(
                $"The upload is incomplete: {received} of {session.SizeBytes} bytes arrived. Send the remaining chunks before completing it.");
        }

        // Move out of uploads/ before importing, so the registry never points into scratch space.
        var destination = Path.Combine(options.CurrentValue.DataDirectory, "models", "uploaded", session.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(path, destination, overwrite: true);
        Cleanup(uploadId);

        try
        {
            var model = await importer.ImportFromPathAsync(destination, name ?? Path.GetFileNameWithoutExtension(session.FileName), copyIntoDataDirectory: false, cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation("Upload {UploadId} completed and registered as {ModelId}.", uploadId, model.Id);
            return model;
        }
        catch (NetCoreAIException)
        {
            // Nothing recognised it: leave the file rather than making the user upload gigabytes again.
            logger.LogWarning("Upload {UploadId} finished at {Path} but no backend recognised it; the file was kept.", uploadId, destination);
            throw;
        }
    }

    public Task AbortAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        Cleanup(uploadId);
        return Task.CompletedTask;
    }

    public Task<UploadSession?> GetAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(uploadId))
        {
            return Task.FromResult<UploadSession?>(null);
        }

        var metadata = Path.Combine(DirectoryFor(uploadId), "upload.json");
        try
        {
            return Task.FromResult(File.Exists(metadata)
                ? JsonSerializer.Deserialize<UploadSession>(File.ReadAllText(metadata), Json)
                : null);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogDebug(ex, "Could not read upload {UploadId}.", uploadId);
            return Task.FromResult<UploadSession?>(null);
        }
    }

    private void Save(UploadSession session)
    {
        var metadata = Path.Combine(DirectoryFor(session.UploadId), "upload.json");
        File.WriteAllText(metadata, JsonSerializer.Serialize(session, Json));
    }

    private void Cleanup(string uploadId)
    {
        if (!IsValidId(uploadId))
        {
            return;
        }

        try
        {
            var directory = DirectoryFor(uploadId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not clean up upload {UploadId}.", uploadId);
        }
    }

    private string DirectoryFor(string uploadId) => Path.Combine(UploadRoot, uploadId);

    private string FilePath(UploadSession session) => Path.Combine(DirectoryFor(session.UploadId), session.FileName);

    /// <summary>Upload ids are server-generated hex; anything else cannot address a directory.</summary>
    private static bool IsValidId(string? uploadId) =>
        uploadId is { Length: 32 } && uploadId.All(char.IsAsciiHexDigit);

    /// <summary>
    /// Reduces a browser-supplied name to a bare file name. The client controls this string, so directory
    /// separators and traversal segments are stripped rather than trusted.
    /// </summary>
    internal static string Sanitize(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim('.', ' ');
        return name.Length == 0 ? "upload.bin" : name;
    }
}
