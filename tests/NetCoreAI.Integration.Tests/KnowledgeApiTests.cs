using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using NetCoreAI.Dashboard.Api;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The knowledge API in a real host: the routes exist, a base can be created and read back, and the
/// caller-tag derivation that decides what a user is allowed to retrieve behaves safely.
/// </summary>
public sealed class KnowledgeApiTests : IAsyncLifetime
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
        });

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
    public async Task A_knowledge_base_can_be_created_read_and_deleted()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _client.PostAsJsonAsync("/netcoreai/api/kb", new
        {
            id = "handbook",
            name = "Employee handbook",
            embeddingModel = "embed",
            format = "Gguf",
        }, ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(ct));

        var listed = await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/kb", ct);
        Assert.Single(listed.GetProperty("knowledgeBases").EnumerateArray());

        // The picker needs to know which source types this host actually has registered.
        var types = listed.GetProperty("sourceTypes").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("files", types);

        var one = await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/kb/handbook", ct);
        Assert.Equal("Employee handbook", one.GetProperty("knowledgeBase").GetProperty("name").GetString());
        Assert.Empty(one.GetProperty("documents").EnumerateArray());

        var deleted = await _client.DeleteAsync("/netcoreai/api/kb/handbook", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/kb", ct)).GetProperty("knowledgeBases").EnumerateArray());
    }

    [Fact]
    public async Task An_unknown_base_is_not_found()
    {
        var response = await _client.GetAsync("/netcoreai/api/kb/nope", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Creating_the_same_base_twice_is_a_bad_request_rather_than_a_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new { id = "dup", name = "Duplicate", embeddingModel = "embed" };
        await _client.PostAsJsonAsync("/netcoreai/api/kb", body, ct);

        var second = await _client.PostAsJsonAsync("/netcoreai/api/kb", body, ct);

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("already exists", await second.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_with_no_query_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync("/netcoreai/api/kb", new { id = "kb", name = "KB", embeddingModel = "embed" }, ct);

        var response = await _client.PostAsJsonAsync("/netcoreai/api/kb/kb/search", new { query = "" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Searching_an_empty_base_returns_no_results_rather_than_failing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync("/netcoreai/api/kb", new { id = "kb", name = "KB", embeddingModel = "embed" }, ct);

        var response = await _client.PostAsJsonAsync("/netcoreai/api/kb/kb/search", new { query = "anything" }, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(body.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task Syncing_a_base_with_no_sources_explains_what_to_do()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync("/netcoreai/api/kb", new { id = "kb", name = "KB", embeddingModel = "embed" }, ct);

        var response = await _client.PostAsync("/netcoreai/api/kb/kb/ingest", null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no enabled data source", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_for_a_base_start_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync("/netcoreai/api/kb", new { id = "kb", name = "KB", embeddingModel = "embed" }, ct);

        Assert.Empty((await _client.GetFromJsonAsync<List<JsonElement>>("/netcoreai/api/kb/kb/jobs", ct))!);
    }

    [Fact]
    public void An_anonymous_caller_gets_no_tags_and_therefore_only_public_documents()
    {
        // Null would mean "no filtering at all". Defaulting to that here would hand every restricted
        // passage to anyone who could reach the endpoint, so anonymous must be an empty list.
        var tags = KnowledgeApi.CallerTags(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.NotNull(tags);
        Assert.Empty(tags);
    }

    [Fact]
    public void Claims_become_access_tags()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "finance"),
            new Claim("tenant", "acme"),
        ], "test");

        var tags = KnowledgeApi.CallerTags(new ClaimsPrincipal(identity));

        // ClaimTypes.Role is a URI; the short name is what someone tagging a document would write.
        Assert.Contains("role:finance", tags);
        Assert.Contains("tenant:acme", tags);
    }
}
