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

    /// <summary>
    /// Version stamped on the dashboard's script and stylesheet URLs, and shown in the sidebar.
    /// </summary>
    /// <remarks>
    /// The informational version, not the assembly version. The assembly version is pinned to the major
    /// release for binding-redirect stability, so before 1.0 it reads 0.0.0 for every build — which made
    /// this a cache-buster that never busted anything. A browser that had once loaded app.js kept it
    /// across upgrades, so dashboard fixes did not reach anyone who had visited before until they forced
    /// a reload. Found by shipping a change to app.js and watching the old file still run.
    /// <para>
    /// Build metadata after "+" is dropped: it is a commit hash, which belongs in neither a URL nor the
    /// corner of a page.
    /// </para>
    /// </remarks>
    public string Version { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } informational
            ? informational.Split('+')[0]
            : Assembly.GetName().Version?.ToString(3) ?? "0";

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
