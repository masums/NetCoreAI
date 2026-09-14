using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetCoreAI.Knowledge;

/// <summary>Helpers shared by the text-shaped extractors.</summary>
internal static class ExtractorHelpers
{
    /// <summary>Reads a stream as UTF-8, honouring a byte-order mark when one is present.</summary>
    public static async Task<string> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool HasExtension(string fileName, params string[] extensions) =>
        extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Plain text and Markdown. Markdown headings become section boundaries, so a retrieved passage can say
/// which section it came from without any format-specific handling downstream.
/// </summary>
internal sealed class PlainTextExtractor : IDocumentExtractor
{
    public bool CanHandle(string fileName, string? contentType) =>
        ExtractorHelpers.HasExtension(fileName, ".txt", ".md", ".markdown", ".log", ".rst", ".adoc")
        || contentType is "text/plain" or "text/markdown";

    public async Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = await ExtractorHelpers.ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        var isMarkdown = ExtractorHelpers.HasExtension(fileName, ".md", ".markdown") || contentType == "text/markdown";

        return new ExtractedDocument
        {
            Title = Title(text, fileName, isMarkdown),

            // A blank file has nothing to index; saying so here gets the caller the specific message
            // about empty or scanned documents rather than a vaguer one from further down the pipeline.
            Sections = string.IsNullOrWhiteSpace(text)
                ? []
                : isMarkdown ? MarkdownSections(text) : [new DocumentSection(text)],
        };
    }

    /// <summary>Splits on ATX headings, keeping each heading with the text beneath it.</summary>
    private static List<DocumentSection> MarkdownSections(string text)
    {
        var sections = new List<DocumentSection>();
        var buffer = new StringBuilder();
        string? heading = null;
        var level = 0;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith('#') && trimmed.TrimStart('#').StartsWith(' '))
            {
                Flush(sections, buffer, heading, level);
                level = trimmed.TakeWhile(c => c == '#').Count();
                heading = trimmed.TrimStart('#').Trim();

                // The heading is kept in the body too: a chunk that says only what is under a heading
                // loses the one line that names the topic.
                buffer.AppendLine(heading);
                continue;
            }

            buffer.AppendLine(trimmed);
        }

        Flush(sections, buffer, heading, level);
        return sections.Count > 0 ? sections : [new DocumentSection(text)];
    }

    private static void Flush(List<DocumentSection> sections, StringBuilder buffer, string? heading, int level)
    {
        var text = buffer.ToString().Trim();
        buffer.Clear();
        if (text.Length > 0)
        {
            sections.Add(new DocumentSection(text) { Heading = heading, HeadingLevel = level });
        }
    }

    /// <summary>A Markdown document's first H1, else the file name.</summary>
    private static string Title(string text, string fileName, bool isMarkdown)
    {
        if (isMarkdown)
        {
            foreach (var line in text.Split('\n').Take(20))
            {
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    return line[2..].Trim();
                }
            }
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }
}

/// <summary>
/// CSV, one row per section. A row is the unit of meaning in tabular data, and each row carries its column
/// names so a retrieved row reads as "Name: Ada, Role: Engineer" rather than a bare tuple.
/// </summary>
internal sealed class CsvExtractor : IDocumentExtractor
{
    public bool CanHandle(string fileName, string? contentType) =>
        ExtractorHelpers.HasExtension(fileName, ".csv", ".tsv") || contentType is "text/csv";

    public async Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = await ExtractorHelpers.ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        var separator = ExtractorHelpers.HasExtension(fileName, ".tsv") ? '\t' : ',';
        var rows = ParseRows(text, separator);
        if (rows.Count == 0)
        {
            return new ExtractedDocument { Title = Path.GetFileNameWithoutExtension(fileName), Sections = [] };
        }

        var headers = rows[0];
        var sections = new List<DocumentSection>(rows.Count - 1);
        for (var i = 1; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            var builder = new StringBuilder();
            for (var column = 0; column < row.Count; column++)
            {
                var name = column < headers.Count ? headers[column] : $"column{column + 1}";
                if (row[column].Length == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(name).Append(": ").Append(row[column]);
            }

            if (builder.Length > 0)
            {
                sections.Add(new DocumentSection(builder.ToString())
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["row"] = i.ToString(CultureInfo.InvariantCulture),
                    },
                });
            }
        }

        return new ExtractedDocument
        {
            Title = Path.GetFileNameWithoutExtension(fileName),
            Sections = sections,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["columns"] = string.Join(", ", headers),
                ["rows"] = sections.Count.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    /// <summary>
    /// A small RFC 4180 reader: quoted fields may contain separators, newlines and doubled quotes.
    /// Pulling in a CSV library for this would add a dependency to Core for one file format.
    /// </summary>
    internal static List<List<string>> ParseRows(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString().Trim());
                    field.Clear();
                    if (row.Any(f => f.Length > 0))
                    {
                        rows.Add(row);
                    }

                    row = [];
                    break;
                default:
                    if (c == separator)
                    {
                        row.Add(field.ToString().Trim());
                        field.Clear();
                    }
                    else
                    {
                        field.Append(c);
                    }

                    break;
            }
        }

        row.Add(field.ToString().Trim());
        if (row.Any(f => f.Length > 0))
        {
            rows.Add(row);
        }

        return rows;
    }
}

/// <summary>
/// JSON. An array becomes one section per element, an object one section per top-level property, both
/// flattened to "path: value" lines so the text carries its own field names into the embedding.
/// </summary>
internal sealed class JsonExtractor : IDocumentExtractor
{
    public bool CanHandle(string fileName, string? contentType) =>
        ExtractorHelpers.HasExtension(fileName, ".json", ".jsonl", ".ndjson") || contentType is "application/json";

    public async Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = await ExtractorHelpers.ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        var sections = new List<DocumentSection>();

        if (ExtractorHelpers.HasExtension(fileName, ".jsonl", ".ndjson"))
        {
            // One JSON document per line, which is how exported datasets usually arrive.
            var line = 0;
            foreach (var entry in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                line++;
                if (TryFlatten(entry, out var flattened))
                {
                    sections.Add(new DocumentSection(flattened)
                    {
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["line"] = line.ToString(CultureInfo.InvariantCulture) },
                    });
                }
            }

            return new ExtractedDocument { Title = Path.GetFileNameWithoutExtension(fileName), Sections = sections };
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new NetCoreAIException($"'{fileName}' is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var element in root.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var flattened = Flatten(element, string.Empty);
                    if (flattened.Length > 0)
                    {
                        sections.Add(new DocumentSection(flattened)
                        {
                            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["index"] = index.ToString(CultureInfo.InvariantCulture) },
                        });
                    }

                    index++;
                }
            }
            else
            {
                var flattened = Flatten(root, string.Empty);
                if (flattened.Length > 0)
                {
                    sections.Add(new DocumentSection(flattened));
                }
            }
        }

        return new ExtractedDocument { Title = Path.GetFileNameWithoutExtension(fileName), Sections = sections };
    }

    private static bool TryFlatten(string json, out string flattened)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            flattened = Flatten(document.RootElement, string.Empty);
            return flattened.Length > 0;
        }
        catch (JsonException)
        {
            // One malformed line in a large export should not fail the whole file.
            flattened = string.Empty;
            return false;
        }
    }

    /// <summary>Renders JSON as "path: value" lines, which embeds better than raw JSON syntax.</summary>
    internal static string Flatten(JsonElement element, string prefix)
    {
        var builder = new StringBuilder();
        Walk(element, prefix, builder);
        return builder.ToString().Trim();
    }

    private static void Walk(JsonElement element, string path, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", builder);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]", builder);
                    index++;
                }

                break;

            case JsonValueKind.Null or JsonValueKind.Undefined:
                break;

            default:
                var value = element.ToString();
                if (value.Length > 0)
                {
                    builder.Append(path.Length == 0 ? value : $"{path}: {value}").Append('\n');
                }

                break;
        }
    }
}
