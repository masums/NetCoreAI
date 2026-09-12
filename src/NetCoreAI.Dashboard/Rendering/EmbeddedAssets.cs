using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;

namespace NetCoreAI.Dashboard.Rendering;

/// <summary>Serves files embedded from wwwroot/ so the host needs no static-file middleware.</summary>
public sealed class EmbeddedAssets
{
    private static readonly Assembly Assembly = typeof(EmbeddedAssets).Assembly;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly Dictionary<string, string> _names = Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal))
        .ToDictionary(n => n["wwwroot/".Length..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    public string Version { get; } = Assembly.GetName().Version?.ToString(3) ?? "0";

    public bool TryGet(string path, out Stream? stream, out string contentType)
    {
        stream = null;
        contentType = "application/octet-stream";
        var key = path.Trim('/').Replace('\\', '/');
        if (!_names.TryGetValue(key, out var name))
        {
            return false;
        }

        stream = Assembly.GetManifestResourceStream(name);
        if (ContentTypes.TryGetContentType(key, out var ct))
        {
            contentType = ct;
        }

        return stream is not null;
    }
}
