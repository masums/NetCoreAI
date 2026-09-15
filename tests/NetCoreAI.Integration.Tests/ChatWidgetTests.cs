using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The embeddable widget: a script tag that puts an agent on one of the host's own pages.
/// </summary>
public sealed class ChatWidgetTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    public async ValueTask InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;

            // Deliberately locked down, so the test below is about the widget script being reachable
            // rather than about the host being open.
            o.Dashboard.AllowAnonymous = false;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_widget_script_is_served()
    {
        using var response = await _app.GetTestClient().GetAsync(new Uri("/netcoreai/_content/widget.js", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_script_is_reachable_on_a_host_that_denies_anonymous_access()
    {
        // It goes on a page the host serves to its own visitors. If fetching it needed a dashboard
        // session, the widget could only ever appear on the dashboard, which is not where it is wanted.
        using var response = await _app.GetTestClient().GetAsync(new Uri("/netcoreai/_content/widget.js", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task What_the_widget_talks_to_is_still_protected()
    {
        // The script being public is not the same as the agent being public. This is the line that
        // matters: the snippet carries no credential, so the run is authorised by the visitor's own
        // session or not at all.
        using var response = await _app.GetTestClient().PostAsync(
            new Uri("/netcoreai/api/agents/helper/run", UriKind.Relative),
            new StringContent("{\"message\":\"hello\"}", System.Text.Encoding.UTF8, "application/json"),
            Ct);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected the run to be refused, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task The_script_carries_no_key_and_asks_for_none()
    {
        var script = await _app.GetTestClient().GetStringAsync(new Uri("/netcoreai/_content/widget.js", UriKind.Relative), Ct);

        // A key in a page is a public key. The widget sends the visitor's existing cookie and nothing
        // else, and there is no attribute for supplying one.
        Assert.Contains("credentials: 'same-origin'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("data-api-key", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_answer_is_written_as_text_rather_than_as_markup()
    {
        var script = await _app.GetTestClient().GetStringAsync(new Uri("/netcoreai/_content/widget.js", UriKind.Relative), Ct);

        // An answer is model output, and a widget that renders it as HTML on somebody else's page is a
        // cross-site scripting hole with a friendly face. This pins the choice rather than the rendering.
        Assert.Contains("answer.textContent +=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("answer.innerHTML", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_is_styled_entirely_through_css_variables()
    {
        var script = await _app.GetTestClient().GetStringAsync(new Uri("/netcoreai/_content/widget.js", UriKind.Relative), Ct);

        // So a host restyles it without touching the file, and without the file needing a theming API.
        foreach (var variable in (string[])["--ncai-accent", "--ncai-bg", "--ncai-text", "--ncai-radius", "--ncai-width"])
        {
            Assert.Contains(variable, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task It_can_be_closed_from_the_keyboard()
    {
        var script = await _app.GetTestClient().GetStringAsync(new Uri("/netcoreai/_content/widget.js", UriKind.Relative), Ct);

        // A fixed panel with no keyboard way out is a trap for anybody not using a mouse.
        Assert.Contains("Escape", script, StringComparison.Ordinal);
    }
}
