using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The hub and download endpoints in a real host. Nothing here reaches the network: the point is that the
/// routes exist, the page renders, offline mode is enforced and a bad request produces a useful message.
/// </summary>
public sealed class HubHostTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private HttpClient _client = default!;
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
            o.Dashboard.AllowAnonymous = true;

            // Offline keeps the suite from touching Hugging Face; the online paths are unit-tested.
            o.Network.OfflineMode = true;
        }).AddGgufBackend();

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_hub_page_renders_and_is_in_the_navigation()
    {
        var html = await _client.GetStringAsync("/netcoreai/hub", TestContext.Current.CancellationToken);

        Assert.Contains("<title>NetCoreAI</title>", html, StringComparison.Ordinal);
        Assert.Contains("Model Hub", html, StringComparison.Ordinal);

        // Offline mode is stated on the page rather than failing silently.
        Assert.Contains("Offline mode is on", html, StringComparison.Ordinal);

        var models = await _client.GetStringAsync("/netcoreai/models", TestContext.Current.CancellationToken);
        Assert.Contains("/netcoreai/hub", models, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_sources_are_listed()
    {
        var sources = await _client.GetFromJsonAsync<List<JsonElement>>("/netcoreai/api/hub/sources", TestContext.Current.CancellationToken);

        Assert.NotNull(sources);
        Assert.Contains(sources!, s => s.GetProperty("id").GetString() == "huggingface");
    }

    [Fact]
    public async Task Downloads_start_empty_and_an_unknown_job_is_not_found()
    {
        var jobs = await _client.GetFromJsonAsync<List<JsonElement>>("/netcoreai/api/downloads", TestContext.Current.CancellationToken);
        Assert.Empty(jobs!);

        var missing = await _client.GetAsync("/netcoreai/api/downloads/nope", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_download_with_no_files_is_rejected_before_anything_is_queued()
    {
        var response = await _client.PostAsJsonAsync(
            "/netcoreai/api/downloads",
            new { repoId = "acme/model", files = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at least one file", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_without_a_path_or_url_explains_what_is_needed()
    {
        var response = await _client.PostAsJsonAsync(
            "/netcoreai/api/models/import",
            new { path = (string?)null, url = (string?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("path on the server", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Importing_a_path_that_no_backend_recognises_says_which_package_is_missing()
    {
        var file = Path.Combine(_dataDir, "not-a-model.txt");
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(file, "hello", TestContext.Current.CancellationToken);

        var response = await _client.PostAsJsonAsync(
            "/netcoreai/api/models/import",
            new { path = file },
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("NetCoreAI.Backend", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browsing_the_hub_while_offline_fails_with_an_offline_message()
    {
        var response = await _client.GetAsync("/netcoreai/api/hub/search?q=qwen", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("Offline mode", body, StringComparison.Ordinal);
    }
}
