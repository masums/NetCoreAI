using System.Globalization;

namespace NetCoreAI.Dashboard.Components;

/// <summary>Formatting helpers shared by pages.</summary>
public static class Fmt
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return "–";
        }

        double b = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (b >= 1024 && i < units.Length - 1)
        {
            b /= 1024;
            i++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{b:0.#} {units[i]}");
    }

    public static string Ago(DateTimeOffset? at)
    {
        if (at is null)
        {
            return "never";
        }

        var span = DateTimeOffset.UtcNow - at.Value;
        return span.TotalSeconds < 60 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
            : at.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Caps(ModelCapabilities c)
    {
        var parts = new List<string>();
        if (c.Supports(ModelCapability.Chat)) { parts.Add("chat"); }
        if (c.Supports(ModelCapability.Embeddings)) { parts.Add("embed"); }
        if (c.Supports(ModelCapability.ToolCalling)) { parts.Add("tools"); }
        if (c.Supports(ModelCapability.Vision)) { parts.Add("vision"); }
        if (c.Supports(ModelCapability.StructuredOutput)) { parts.Add("json-schema"); }
        else if (c.Supports(ModelCapability.JsonMode)) { parts.Add("json"); }
        return parts.Count == 0 ? "–" : string.Join(" · ", parts);
    }
}
