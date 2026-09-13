using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// The REST data source against a real endpoint: what it pulls out of a JSON response, and what it says
/// when the response is not shaped the way the settings claim.
/// </summary>
public class RestApiDataSourceTests : IAsyncDisposable
{
    private WebApplication? _server;
    private Microsoft.Extensions.Hosting.IHost? _host;

    /// <summary>Starts an endpoint returning <paramref name="json"/>, and a source pointed at it.</summary>
    private async Task<(IDataSource Source, DataSourceDefinition Definition)> StartAsync(
        string json,
        Dictionary<string, string>? settings = null,
        int status = 200,
        string contentType = "application/json")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();

        _server.MapGet("/items", async (HttpContext http) =>
        {
            // Echo the auth header back so a test can prove it was sent.
            if (http.Request.Headers.Authorization.ToString() is { Length: > 0 } auth)
            {
                http.Response.Headers["X-Seen-Authorization"] = auth;
            }

            http.Response.StatusCode = status;
            http.Response.ContentType = contentType;
            await http.Response.WriteAsync(json, http.RequestAborted);
        });

        await _server.StartAsync();
        _host = await TestHost.StartAsync();

        var source = _host.Services.GetServices<IDataSource>().Single(s => s.Type == RestApiDataSource.TypeName);
        var all = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RestApiDataSource.UrlSetting] = $"{_server.Urls.First()}/items",
        };

        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        return (source, new DataSourceDefinition
        {
            Id = "rest1",
            KnowledgeBaseId = "kb1",
            Name = "Tickets",
            Type = RestApiDataSource.TypeName,
            Settings = all,
        });
    }

    private static async Task<List<SourceDocument>> CollectAsync(IDataSource source, DataSourceDefinition definition, CancellationToken cancellationToken)
    {
        var documents = new List<SourceDocument>();
        await foreach (var document in source.EnumerateAsync(definition, cancellationToken))
        {
            documents.Add(document);
        }

        return documents;
    }

    private static async Task<string> ReadAsync(SourceDocument document, CancellationToken cancellationToken)
    {
        await using var stream = await document.OpenAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    [Fact]
    public async Task Items_are_read_from_a_path_into_the_response()
    {
        var json = """
            {"data":{"items":[
              {"id":"T-1","subject":"Printer offline","body":"The printer on floor two is offline."},
              {"id":"T-2","subject":"VPN drops","body":"The VPN disconnects every ten minutes."}
            ]}}
            """;

        var (source, definition) = await StartAsync(json, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RestApiDataSource.ItemsPathSetting] = "data.items",
            [RestApiDataSource.IdPathSetting] = "id",
            [RestApiDataSource.TitlePathSetting] = "subject",
            [RestApiDataSource.ContentPathSetting] = "body",
        });

        var documents = await CollectAsync(source, definition, TestContext.Current.CancellationToken);

        Assert.Equal(2, documents.Count);
        Assert.Equal("T-1", documents[0].Id);
        Assert.Equal("Printer offline", documents[0].Title);
        Assert.Equal("The printer on floor two is offline.", await ReadAsync(documents[0], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_bare_array_response_needs_no_items_path()
    {
        var (source, definition) = await StartAsync("""[{"id":"1","text":"first"},{"id":"2","text":"second"}]""");

        var documents = await CollectAsync(source, definition, TestContext.Current.CancellationToken);

        Assert.Equal(2, documents.Count);
    }

    [Fact]
    public async Task Without_a_content_path_the_whole_item_is_flattened()
    {
        var (source, definition) = await StartAsync("""[{"id":"1","name":"Ada","role":"Engineer"}]""");

        var text = await ReadAsync(Assert.Single(await CollectAsync(source, definition, TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);

        // Field names reach the embedding, so "who is the engineer" can match.
        Assert.Contains("name: Ada", text, StringComparison.Ordinal);
        Assert.Contains("role: Engineer", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_with_no_content_are_skipped_rather_than_indexed_empty()
    {
        var (source, definition) = await StartAsync(
            """[{"id":"1","body":"real content here"},{"id":"2","body":""}]""",
            new Dictionary<string, string>(StringComparer.Ordinal) { [RestApiDataSource.ContentPathSetting] = "body" });

        Assert.Single(await CollectAsync(source, definition, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_items_path_says_which_path_was_not_found()
    {
        var (source, definition) = await StartAsync(
            """{"data":{}}""",
            new Dictionary<string, string>(StringComparer.Ordinal) { [RestApiDataSource.ItemsPathSetting] = "data.results" });

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await CollectAsync(source, definition, TestContext.Current.CancellationToken));

        Assert.Contains("data.results", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_endpoint_is_reported_with_its_status()
    {
        var (source, definition) = await StartAsync("""{"error":"nope"}""", status: 403);

        var result = await source.TestAsync(definition, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("403", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_that_is_not_json_is_reported_as_such()
    {
        var (source, definition) = await StartAsync("<html>not json</html>", contentType: "text/html");

        var result = await source.TestAsync(definition, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("valid JSON", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_a_working_endpoint_reports_a_sample()
    {
        var (source, definition) = await StartAsync(
            """[{"id":"1","subject":"First"},{"id":"2","subject":"Second"}]""",
            new Dictionary<string, string>(StringComparer.Ordinal) { [RestApiDataSource.TitlePathSetting] = "subject" });

        var result = await source.TestAsync(definition, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, result.DocumentCount);
        Assert.Contains("First", result.SampleTitles);
    }

    [Fact]
    public async Task A_source_with_no_url_says_so_before_making_a_request()
    {
        var (source, definition) = await StartAsync("[]");
        var without = definition with { Settings = new Dictionary<string, string>(StringComparer.Ordinal) };

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await CollectAsync(source, without, TestContext.Current.CancellationToken));

        Assert.Contains("no valid URL", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.b", "2")]
    [InlineData("list[0]", "\"first\"")]
    [InlineData("list[1]", "\"second\"")]
    [InlineData("a.missing", "")]
    [InlineData("list[9]", "")]
    [InlineData("a.b.c", "")]
    public void Json_paths_walk_objects_and_array_indexes(string path, string expected)
    {
        using var document = JsonDocument.Parse("""{"a":{"b":2},"list":["first","second"]}""");

        var element = RestApiDataSource.Navigate(document.RootElement, path);

        // A path that does not resolve returns undefined rather than throwing; the caller reports it.
        Assert.Equal(expected, element.ValueKind == JsonValueKind.Undefined ? "" : element.GetRawText());
    }

    [Fact]
    public void An_empty_path_returns_the_element_itself()
    {
        using var document = JsonDocument.Parse("""{"a":1}""");

        Assert.Equal("a", RestApiDataSource.Navigate(document.RootElement, "").EnumerateObject().Single().Name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.SuppressFinalize(this);
    }
}
