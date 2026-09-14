using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace NetCoreAI.Dashboard.Rendering;

/// <summary>
/// Renders a model card to HTML for the Hub. The markdown comes from a third-party repository, so raw HTML
/// is stripped rather than trusted: a model card is the one place in NetCoreAI where a stranger's markup
/// would otherwise reach the dashboard.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseListExtras()
        .UseTaskLists()
        // No raw HTML, and therefore no <script>, <iframe> or event handlers out of a model card.
        .DisableHtml()
        .Build();

    /// <summary>
    /// Renders markdown to HTML. Relative links and images are rewritten against <paramref name="baseUrl"/>
    /// so a model card's own screenshots resolve instead of 404ing against the dashboard.
    /// </summary>
    public static string ToHtml(string? markdown, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(markdown, Pipeline);
        Sanitize(document, baseUrl?.TrimEnd('/'));

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// Makes every link safe to render: dangerous schemes are dropped, and repo-relative paths are pointed
    /// back at the repository. Disabling raw HTML is not enough on its own, because a markdown link target
    /// is attacker-controlled too — <c>[click](javascript:…)</c> is ordinary markdown.
    /// </summary>
    private static void Sanitize(MarkdownDocument document, string? baseUrl)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.Url is not { Length: > 0 } url)
            {
                continue;
            }

            if (url.StartsWith('#'))
            {
                continue;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            {
                // Only schemes that cannot execute survive; anything else becomes inert.
                link.Url = absolute.Scheme is "http" or "https" or "mailto" ? url : "#";
                continue;
            }

            link.Url = baseUrl is { Length: > 0 } ? $"{baseUrl}/{url.TrimStart('/')}" : "#";
        }
    }
}
