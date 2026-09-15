using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Knowledge;

/// <summary>
/// Files on disk: the ones uploaded into a knowledge base, or a folder the server can read.
/// </summary>
/// <remarks>
/// Uploaded files live under <c>{DataDirectory}/kb/{kbId}/files</c> and belong to the base. A folder
/// outside that is read in place and never written to, so pointing a base at a shared drive does not risk
/// the originals.
/// </remarks>
internal sealed class FileDataSource(IOptionsMonitor<NetCoreAIOptions> options, NetCoreAI.Tenancy.ITenantAccessor tenants, ILogger<FileDataSource> logger) : IDataSource
{
    public const string TypeName = "files";

    /// <summary>Setting naming a folder to read instead of the base's own upload folder.</summary>
    public const string FolderSetting = "folder";

    /// <summary>Setting holding a semicolon-separated list of glob patterns; empty means every file.</summary>
    public const string PatternSetting = "pattern";

    /// <summary>Whether to descend into subfolders. Defaults to true.</summary>
    public const string RecursiveSetting = "recursive";

    public string Type => TypeName;

    public async IAsyncEnumerable<SourceDocument> EnumerateAsync(DataSourceDefinition definition, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var folder = ResolveFolder(definition);
        if (!Directory.Exists(folder))
        {
            // An upload folder that does not exist yet simply has no documents.
            logger.LogDebug("Folder {Folder} does not exist; the source has no documents yet.", folder);
            yield break;
        }

        foreach (var path in Files(definition, folder))
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not read {Path}; skipping it.", path);
                continue;
            }

            yield return new SourceDocument(RelativeId(folder, path), Path.GetFileNameWithoutExtension(path))
            {
                FileName = Path.GetFileName(path),
                ContentType = ContentType(path),
                SizeBytes = info.Length,
                Source = path,
                ModifiedAt = info.LastWriteTimeUtc,
                OpenAsync = _ => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true)),
            };
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<DataSourceTestResult> TestAsync(DataSourceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var folder = ResolveFolder(definition);
        if (!Directory.Exists(folder))
        {
            return Task.FromResult(new DataSourceTestResult(
                false,
                $"{folder} does not exist, or the server cannot see it. Remember the path is read by the server, not by your browser."));
        }

        try
        {
            var files = Files(definition, folder).Take(200).ToList();
            return Task.FromResult(new DataSourceTestResult(
                true,
                files.Count == 0
                    ? $"{folder} is readable but holds no matching files."
                    : $"Found {files.Count} file(s) in {folder}.",
                files.Count)
            {
                SampleTitles = [.. files.Take(5).Select(Path.GetFileName)!],
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new DataSourceTestResult(false, $"{folder} could not be read: {ex.Message}"));
        }
    }

    /// <summary>The folder a base's uploads live in.</summary>
    /// <summary>Everything one tenant has uploaded, across all of its knowledge bases.</summary>
    public static string UploadRoot(string dataDirectory, string? tenantId = null) =>
        tenantId is null or NetCoreAI.Tenancy.TenantId.Default
            ? Path.Combine(dataDirectory, "kb")
            : Path.Combine(dataDirectory, "tenants", tenantId, "kb");

    public static string UploadFolder(string dataDirectory, string knowledgeBaseId, string? tenantId = null) =>
        tenantId is null or NetCoreAI.Tenancy.TenantId.Default

            // The path a single-tenant host already uses. Unchanged, so switching tenancy on does not
            // orphan every file already uploaded.
            ? Path.Combine(dataDirectory, "kb", knowledgeBaseId, "files")
            : Path.Combine(dataDirectory, "tenants", tenantId, "kb", knowledgeBaseId, "files");

    private string ResolveFolder(DataSourceDefinition definition) =>
        definition.Settings.TryGetValue(FolderSetting, out var folder) && folder is { Length: > 0 }
            ? Path.GetFullPath(folder)
            : UploadFolder(options.CurrentValue.DataDirectory, definition.KnowledgeBaseId, tenants.Current);

    private static IEnumerable<string> Files(DataSourceDefinition definition, string folder)
    {
        var recursive = !definition.Settings.TryGetValue(RecursiveSetting, out var value) || !bool.TryParse(value, out var flag) || flag;
        var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var patterns = definition.Settings.TryGetValue(PatternSetting, out var raw) && raw is { Length: > 0 }
            ? raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["*"];

        // Several patterns can match one file ("*.pdf;*.*"), and ingesting it twice would double its chunks.
        return patterns
            .SelectMany(pattern => Directory.EnumerateFiles(folder, pattern, search))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path relative to the folder, so a file keeps its identity across syncs and a re-sync updates
    /// the document rather than adding a second copy.
    /// </summary>
    private static string RelativeId(string folder, string path) =>
        Path.GetRelativePath(folder, path).Replace('\\', '/');

    private static string? ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".html" or ".htm" => "text/html",
        ".md" or ".markdown" => "text/markdown",
        ".csv" => "text/csv",
        ".json" or ".jsonl" or ".ndjson" => "application/json",
        ".txt" or ".log" => "text/plain",
        _ => null,
    };
}
