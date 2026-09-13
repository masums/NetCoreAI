using System.Text;
using AngleSharp;
using AngleSharp.Dom;

namespace NetCoreAI.Documents;

/// <summary>
/// HTML, reduced to the text a reader would actually see.
/// </summary>
/// <remarks>
/// Script, style, navigation and footer elements are dropped before any text is taken: a page's chrome
/// repeats on every document in a crawl, and indexing it means every query matches the navigation menu.
/// Headings become section boundaries so citations can name where a passage sits in the page.
/// </remarks>
public sealed class HtmlExtractor : IDocumentExtractor
{
    /// <summary>Elements that carry no document content, removed before extraction.</summary>
    private static readonly string[] Noise = ["script", "style", "noscript", "nav", "header", "footer", "aside", "form", "svg", "iframe"];

    private static readonly string[] Headings = ["h1", "h2", "h3", "h4", "h5", "h6"];

    public int Priority => 10;

    public bool CanHandle(string fileName, string? contentType) =>
        Path.GetExtension(fileName) is { Length: > 0 } extension && (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        || contentType is "text/html" or "application/xhtml+xml";

    public async Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var context = BrowsingContext.New(Configuration.Default);
        using var document = await context.OpenAsync(request => request.Content(content), cancellationToken).ConfigureAwait(false);

        foreach (var selector in Noise)
        {
            foreach (var element in document.QuerySelectorAll(selector).ToList())
            {
                element.Remove();
            }
        }

        // Prefer the semantic content root when the page has one; it is what strips the remaining chrome.
        var root = document.QuerySelector("main") ?? document.QuerySelector("article") ?? document.Body;
        if (root is null)
        {
            throw new NetCoreAIException($"'{fileName}' has no body content to extract.");
        }

        var sections = new List<DocumentSection>();
        var buffer = new StringBuilder();
        string? heading = null;
        var level = 0;

        foreach (var element in root.QuerySelectorAll("h1, h2, h3, h4, h5, h6, p, li, td, th, pre, blockquote"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = Normalize(element.TextContent);
            if (text.Length == 0)
            {
                continue;
            }

            if (Headings.Contains(element.LocalName, StringComparer.OrdinalIgnoreCase))
            {
                Flush(sections, buffer, heading, level);
                heading = text;
                level = int.Parse(element.LocalName[1..], System.Globalization.CultureInfo.InvariantCulture);
                buffer.AppendLine(text);
                continue;
            }

            buffer.AppendLine(text);
        }

        Flush(sections, buffer, heading, level);

        if (sections.Count == 0 && Normalize(root.TextContent) is { Length: > 0 } fallback)
        {
            // A page built entirely from divs has none of the elements above; take its text as one section.
            sections.Add(new DocumentSection(fallback));
        }

        return new ExtractedDocument
        {
            Title = Title(document, fileName),
            Sections = sections,
            Metadata = Metadata(document),
        };
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

    /// <summary>Collapses the whitespace HTML is full of into single spaces.</summary>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Title(IDocument document, string fileName)
    {
        if (document.Title is { Length: > 0 } title)
        {
            return Normalize(title);
        }

        var h1 = document.QuerySelector("h1")?.TextContent;
        return h1 is { Length: > 0 } ? Normalize(h1) : Path.GetFileNameWithoutExtension(fileName);
    }

    private static Dictionary<string, string> Metadata(IDocument document)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in (string[])["description", "author", "keywords"])
        {
            if (document.QuerySelector($"meta[name={name}]")?.GetAttribute("content") is { Length: > 0 } value)
            {
                metadata[name] = Normalize(value);
            }
        }

        if (document.QuerySelector("link[rel=canonical]")?.GetAttribute("href") is { Length: > 0 } canonical)
        {
            metadata["canonical"] = canonical;
        }

        return metadata;
    }
}
