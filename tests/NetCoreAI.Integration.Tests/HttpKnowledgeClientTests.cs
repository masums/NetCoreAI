using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Client;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// <c>IKnowledgeClient</c> over HTTP, against a real host: the same interface application code uses
/// in-process, driven through the API rather than through the services behind it.
/// </summary>
/// <remarks>
/// The client is resolved from <c>AddNetCoreAIClient</c> rather than constructed here, so the registration
/// a host would actually write is part of what is tested.
/// </remarks>
public sealed class HttpKnowledgeClientTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private ServiceProvider _client = default!;
    private string _dataDir = "";

    /// <summary>Enough text that the chunker keeps it: a one-line document is below the minimum.</summary>
    private const string Prose =
        "Every employee is entitled to twenty five days of paid holiday each year, in addition to public holidays. "
        + "Holiday is booked through the staff portal and needs a manager's approval before it is confirmed. "
        + "Up to five unused days may be carried into the following year, and anything beyond that is lost.";

    private IKnowledgeClient Knowledge => _client.GetRequiredService<IKnowledgeClient>();

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

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetCoreAIClient(o => o.BaseUrl = new Uri("http://localhost/netcoreai"));

        // The only thing swapped out is the transport: requests go to the in-memory host instead of a socket.
        services.AddHttpClient(NetCoreAIClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _app.GetTestServer().CreateHandler());

        _client = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
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

    private async Task<string> CreateBaseAsync(string id, CancellationToken cancellationToken)
    {
        var http = _app.GetTestClient();
        var response = await http.PostAsJsonAsync("/netcoreai/api/kb", new { id, name = id, embeddingModel = "embed" }, cancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        return id;
    }

    [Fact]
    public async Task Bases_can_be_listed_and_fetched_through_the_client()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateBaseAsync("handbook", ct);

        Assert.Single(await Knowledge.ListAsync(ct));
        Assert.Equal("handbook", (await Knowledge.GetAsync("handbook", ct))?.Id);
    }

    [Fact]
    public async Task An_unknown_base_comes_back_as_null_rather_than_an_error()
    {
        // The in-process client returns null here, and code written against the interface must not have to
        // catch on one side and check for null on the other.
        Assert.Null(await Knowledge.GetAsync("nope", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Searching_an_empty_base_returns_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateBaseAsync("kb", ct);

        Assert.Empty(await Knowledge.SearchAsync("kb", "anything", cancellationToken: ct));
    }

    [Fact]
    public async Task A_client_cannot_declare_its_own_access_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateBaseAsync("kb", ct);

        // Honouring these would let any client read every restricted passage by asking for "*". Refusing
        // loudly is the only safe answer: silently ignoring them would look like it worked.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Knowledge.SearchAsync("kb", "anything", callerTags: ["*"], cancellationToken: ct));

        Assert.Contains("API key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_from_the_host_carries_the_hosts_own_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateBaseAsync("kb", ct);

        // The host explains that the base has no sources; the client must not flatten that into "400".
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() => Knowledge.SyncAsync("kb", ct));

        Assert.Contains("no enabled data source", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_host_names_the_url_it_tried()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetCoreAIClient(o =>
        {
            o.BaseUrl = new Uri("http://127.0.0.1:1/netcoreai");
            o.Timeout = TimeSpan.FromSeconds(5);
        });

        await using var provider = services.BuildServiceProvider();

        // A client app pointed at the wrong base URL is the commonest cause of a connection failure, so
        // the message has to say which URL was tried rather than only that one was refused.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            provider.GetRequiredService<IKnowledgeClient>().ListAsync(TestContext.Current.CancellationToken));

        Assert.Contains("127.0.0.1:1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingesting_through_the_client_uploads_the_content_under_the_documents_own_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateBaseAsync("kb", ct);

        // This host has no embedding model registered, so ingestion gets as far as needing one and stops.
        // What is being checked here is the leg before that: the bytes reached the base's own folder,
        // under the name the document carried, rather than being read into memory or renamed on the way.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Knowledge.IngestTextAsync("kb", "policy-1", "Holiday policy", Prose, cancellationToken: ct));

        var uploaded = Path.Combine(_dataDir, "kb", "kb", "files", "policy-1.txt");
        Assert.Equal(Prose, await File.ReadAllTextAsync(uploaded, ct));
        Assert.Contains("embed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_document_that_is_not_there_is_not_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateBaseAsync("kb", ct);

        await Knowledge.DeleteDocumentAsync("kb", "never-existed", ct);
    }
}
