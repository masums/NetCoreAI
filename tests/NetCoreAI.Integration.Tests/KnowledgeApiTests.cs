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
        })
            // Pooling off: a pooled SQLite connection keeps the database file open after the test
            // that made it is done, and the process-global ClearAllPools() that used to compensate
            // disposed connections belonging to other test classes running in parallel.
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();
        _client = _app.GetTestClient();
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
    public async Task The_knowledge_page_renders_and_is_in_the_navigation()
    {
        var ct = TestContext.Current.CancellationToken;

        var html = await _client.GetStringAsync("/netcoreai/knowledge", ct);
        Assert.Contains("Knowledge bases", html, StringComparison.Ordinal);

        // With no embedding model registered, the page says so rather than offering a base that cannot index.
        Assert.Contains("An embedding model is needed first", html, StringComparison.Ordinal);

        var overview = await _client.GetStringAsync("/netcoreai", ct);
        Assert.Contains("/netcoreai/knowledge", overview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chat_page_offers_retrieval_settings_once_there_is_a_base_to_search()
    {
        var ct = TestContext.Current.CancellationToken;

        // Nothing to retrieve from, so nothing to tune: the controls would only be confusing.
        Assert.DoesNotContain("id=\"r-debug\"", await _client.GetStringAsync("/netcoreai/chat", ct), StringComparison.Ordinal);

        await _client.PostAsJsonAsync("/netcoreai/api/kb", new { id = "handbook", name = "Handbook", embeddingModel = "embed" }, ct);

        var html = await _client.GetStringAsync("/netcoreai/chat", ct);
        Assert.Contains("id=\"r-topk\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"r-minscore\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"r-debug\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bases_chunking_and_retrieval_settings_can_be_edited()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync("/netcoreai/api/kb", new { id = "handbook", name = "Handbook", embeddingModel = "embed" }, ct);
        var before = (await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/kb/handbook", ct)).GetProperty("knowledgeBase");

        // The settings form sends the whole record back with the edited fields replaced, so this is the
        // shape the endpoint actually receives.
        var edited = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
        {
            id = "handbook",
            name = "Employee handbook",
            embeddingModel = before.GetProperty("embeddingModel").GetString(),
            vectorStoreId = before.GetProperty("vectorStoreId").GetString(),
            chunking = new { strategy = "Sentence", maxTokens = 256, overlapTokens = 32, minTokens = 16 },
            retrieval = new { topK = 8, minScore = 0.5 },
            defaultAclTags = new[] { "role:hr" },
        }));

        var response = await _client.PutAsJsonAsync("/netcoreai/api/kb/handbook", edited, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));

        var after = (await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/kb/handbook", ct)).GetProperty("knowledgeBase");
        Assert.Equal("Employee handbook", after.GetProperty("name").GetString());
        Assert.Equal("Sentence", after.GetProperty("chunking").GetProperty("strategy").GetString());
        Assert.Equal(256, after.GetProperty("chunking").GetProperty("maxTokens").GetInt32());
        Assert.Equal(8, after.GetProperty("retrieval").GetProperty("topK").GetInt32());
        Assert.Equal("role:hr", after.GetProperty("defaultAclTags")[0].GetString());
    }

    [Fact]
    public async Task The_knowledge_page_offers_the_fields_each_source_type_needs()
    {
        var html = await _client.GetStringAsync("/netcoreai/knowledge", TestContext.Current.CancellationToken);

        // A SQL source is unusable without these, and shipping the source with no way to configure it
        // would leave it reachable only through the API.
        Assert.Contains("name=\"connectionString\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"query\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"schedule\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_can_be_listed_and_an_unknown_one_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty((await _client.GetFromJsonAsync<List<JsonElement>>("/netcoreai/api/jobs", ct))!);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/netcoreai/api/jobs/nope", ct)).StatusCode);
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
