using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>Spins up a real host (TestServer) with NetCoreAI mounted next to an unrelated host endpoint.</summary>
public sealed class DashboardHostTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private HttpClient _client = default!;
    private FakeOpenAIServer _openai = default!;
    private string _dataDir = "";

    public async ValueTask InitializeAsync()
    {
        _openai = await FakeOpenAIServer.StartAsync();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
        .AddOpenAICompatibleBackend()
        .AddAnthropicBackend()
        .AddOllamaBackend();

        _app = builder.Build();
        _app.MapGet("/host", () => "host endpoint untouched");
        _app.MapNetCoreAI();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _openai.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Host_endpoints_are_untouched_and_dashboard_pages_render()
    {
        Assert.Equal("host endpoint untouched", await _client.GetStringAsync("/host"));
        foreach (var page in new[] { "/netcoreai", "/netcoreai/models", "/netcoreai/providers", "/netcoreai/chat", "/netcoreai/hardware", "/netcoreai/settings" })
        {
            var html = await _client.GetStringAsync(page);
            Assert.Contains("<title>NetCoreAI</title>", html);
            Assert.Contains("app.js", html);
        }

        var css = await _client.GetAsync("/netcoreai/_content/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Meta_package_auto_registers_sqlite_storage_and_vector_store()
    {
        Assert.IsType<Storage.Sqlite.SqliteMetadataStore>(_app.Services.GetRequiredService<IMetadataStore>());
        Assert.IsType<VectorStores.Sqlite.SqliteVectorStore>(_app.Services.GetRequiredService<IVectorStore>());
        Assert.True(File.Exists(Path.Combine(_dataDir, "netcoreai.db")));
    }

    [Fact]
    public async Task Health_reports_store_and_readiness()
    {
        var response = await _client.GetAsync("/netcoreai/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("ready").GetBoolean());
        Assert.Equal("ok", json.GetProperty("metadataStore").GetString());
    }

    [Fact]
    public async Task Providers_endpoint_lists_the_three_remote_backends_with_presets()
    {
        var providers = await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/providers");
        var ids = providers.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Equal(["openai", "anthropic", "ollama"], ids);
        Assert.Contains(providers.EnumerateArray(), p => p.GetProperty("id").GetString() == "openai" && p.GetProperty("presets").GetArrayLength() >= 9);
    }

    [Fact]
    public async Task Add_openai_compatible_connection_from_api_then_chat_with_streaming_without_restart()
    {
        // 1. create connection (secret never comes back)
        var create = await _client.PostAsJsonAsync("/netcoreai/api/providers/connections", new { name = "Fake OpenAI", providerId = "openai", preset = "custom", baseUrl = _openai.BaseUrl, secret = "sk-test" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var connection = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = connection.GetProperty("id").GetString()!;
        Assert.True(connection.GetProperty("hasSecret").GetBoolean());
        Assert.False(connection.TryGetProperty("protectedSecret", out _));

        // 2. test + sync
        var test = await (await _client.PostAsync($"/netcoreai/api/providers/connections/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(test.GetProperty("success").GetBoolean(), test.ToString());
        var synced = await (await _client.PostAsync($"/netcoreai/api/providers/connections/{id}/models", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, synced.GetArrayLength());
        var chatModelId = synced.EnumerateArray().First(m => m.GetProperty("remoteModelId").GetString() == "fake-chat").GetProperty("id").GetString()!;

        // 3. models list shows them with the default alias assigned automatically
        var models = await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/models");
        Assert.Contains(models.GetProperty("aliases").EnumerateArray(), a => a.GetProperty("alias").GetString() == "default");

        // 4. chat: SSE stream
        var request = new HttpRequestMessage(HttpMethod.Post, "/netcoreai/api/chat") { Content = JsonContent.Create(new { model = chatModelId, message = "hello world", parameters = new { temperature = 0.1 } }) };
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: session", sse);
        Assert.Contains("event: delta", sse);
        Assert.Contains("event: done", sse);
        Assert.Contains("echo:", sse);
        Assert.DoesNotContain("event: error", sse);

        // provider saw our key and parameters
        var seen = _openai.Requests.Last();
        Assert.Equal("fake-chat", seen["model"]!.ToString());
        Assert.Equal(0.1, seen["temperature"]!.GetValue<double>(), 3);

        // 5. sessions persisted with usage stats
        var sessions = await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/sessions");
        var sessionId = sessions.EnumerateArray().Single().GetProperty("id").GetString();
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/netcoreai/api/sessions/{sessionId}");
        var messages = detail.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal(2, messages[1].GetProperty("outputTokens").GetInt32());

        // 6. IChatClient from host code hits the same model
        var text = (await _app.Services.GetRequiredService<IChatClient>().GetResponseAsync("from code")).Text;
        Assert.Equal("echo: from code", text);

        // 7. embeddings through the alias
        var embeddings = await _app.Services.GetRequiredService<IChatClientFactory>().GetEmbeddingGenerator(synced.EnumerateArray().First(m => m.GetProperty("remoteModelId").GetString() == "fake-embed").GetProperty("id").GetString()!).GenerateAsync(["abc"]);
        Assert.Equal(3, embeddings[0].Vector.Length);
    }

    [Fact]
    public async Task Wrong_key_is_reported_as_auth_failure()
    {
        var create = await _client.PostAsJsonAsync("/netcoreai/api/providers/connections", new { name = "Bad key", providerId = "openai", preset = "custom", baseUrl = _openai.BaseUrl, secret = "wrong" });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var test = await (await _client.PostAsync($"/netcoreai/api/providers/connections/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(test.GetProperty("success").GetBoolean());
        Assert.Equal("AuthFailed", test.GetProperty("health").GetString());
    }

    [Fact]
    public async Task Settings_roundtrip_including_write_only_hugging_face_token()
    {
        var put = await _client.PutAsJsonAsync("/netcoreai/api/settings", new { huggingFaceToken = "hf_abc", offlineMode = true, dashboardTitle = "My AI" });
        var settings = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(settings.GetProperty("hasHuggingFaceToken").GetBoolean());
        Assert.True(settings.GetProperty("offlineMode").GetBoolean());
        Assert.False(settings.TryGetProperty("huggingFaceToken", out _));
        Assert.Contains("<title>My AI</title>", await _client.GetStringAsync("/netcoreai/settings"));
        await _client.PutAsJsonAsync("/netcoreai/api/settings", new { offlineMode = false, dashboardTitle = "NetCoreAI" });
    }
}

public sealed class DefaultDenyTests
{
    [Fact]
    public async Task Without_authorization_config_every_dashboard_request_is_403_with_guidance()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o => o.DataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N")));
        await using var app = builder.Build();
        app.MapNetCoreAI();
        await app.StartAsync();
        var client = app.GetTestClient();

        var page = await client.GetAsync("/netcoreai");
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("Dashboard.Authorization", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/netcoreai/api/models")).StatusCode);
        // health stays reachable for probes
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/netcoreai/health")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void MapNetCoreAI_without_AddNetCoreAI_fails_fast()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.MapNetCoreAI());
    }
}
