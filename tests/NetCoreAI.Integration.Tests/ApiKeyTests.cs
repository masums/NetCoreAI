using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Agents;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Providers;
using NetCoreAI.Security;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// API keys: what is stored, what is refused, and what a key is allowed to reach once it is accepted.
/// </summary>
public sealed class ApiKeyTests : IAsyncLifetime
{
    private FakeOpenAIServer _openAI = default!;
    private WebApplication _app = default!;
    private string _dataDir = "";

    private IApiKeyService Keys => _app.Services.GetRequiredService<IApiKeyService>();

    public async ValueTask InitializeAsync()
    {
        _openAI = await FakeOpenAIServer.StartAsync();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();

        // How a host opens its API to other applications: add the scheme, and let the dashboard policy
        // accept it alongside whatever signs people in.
        builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName).AddNetCoreAIApiKey();
        builder.Services.AddAuthorization();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.Authorization = p => p
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser();
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapNetCoreAI();
        await _app.StartAsync();

        var connection = await _app.Services.GetRequiredService<IConnectionManager>().SaveAsync(
            new ProviderConnection { Id = "fake", Name = "Fake", ProviderId = OpenAICompatibleProvider.ProviderId, BaseUrl = _openAI.BaseUrl },
            "sk-test",
            TestContext.Current.CancellationToken);

        await _app.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "default",
            Name = "Fake chat",
            Format = ModelFormat.Remote,
            ProviderId = OpenAICompatibleProvider.ProviderId,
            ConnectionId = connection.Id,
            RemoteModelId = "fake-chat",
            Capabilities = new ModelCapabilities(ModelCapability.Chat),
        }, TestContext.Current.CancellationToken);

        await _app.Services.GetRequiredService<IAgentService>().SaveAsync(
            new AgentDefinition { Id = "helper", Name = "Helper", Model = "default" },
            TestContext.Current.CancellationToken);

        await _app.Services.GetRequiredService<IAgentService>().SaveAsync(
            new AgentDefinition { Id = "other", Name = "Other", Model = "default" },
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _openAI.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<CreatedApiKey> KeyAsync(Action<ApiKey>? _ = null, params string[] agentIds) =>
        await Keys.CreateAsync(new ApiKey { Id = "", Name = "Test key", Hash = "", Prefix = "", AgentIds = agentIds }, Ct);

    private HttpClient ClientWith(string secret)
    {
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    // ---------- what is stored ----------

    [Fact]
    public async Task A_new_key_returns_its_secret_once_and_never_stores_it()
    {
        var created = await KeyAsync();

        Assert.StartsWith("ncai_", created.Secret, StringComparison.Ordinal);

        // A stolen database must not yield working keys, so what is kept is a hash and a prefix.
        var stored = Assert.Single(await Keys.ListAsync(Ct));
        Assert.DoesNotContain(created.Secret, stored.Hash, StringComparison.Ordinal);
        Assert.Equal(created.Secret[..12], stored.Prefix);
    }

    [Fact]
    public async Task The_api_never_returns_the_hash()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);
        var body = await ClientWith(created.Secret).GetStringAsync("/netcoreai/api/keys", Ct);

        // Not the secret, but the only thing between a leaked backup and a working key.
        Assert.DoesNotContain(created.Key.Hash, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(created.Key.Prefix, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_cannot_replace_a_keys_secret()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);

        await Keys.UpdateAsync(created.Key with { Hash = "0000", Prefix = "ncai_evil", Name = "Renamed" }, Ct);

        // Otherwise anyone who could edit a key could set its secret to one they knew.
        var stored = Assert.Single(await Keys.ListAsync(Ct));
        Assert.Equal(created.Key.Hash, stored.Hash);
        Assert.Equal("Renamed", stored.Name);
    }

    [Fact]
    public async Task A_key_with_no_name_is_refused()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Keys.CreateAsync(new ApiKey { Id = "", Name = " ", Hash = "", Prefix = "" }, Ct));

        Assert.Contains("name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- what is refused ----------

    [Fact]
    public async Task An_unknown_key_is_refused()
    {
        var response = await ClientWith("ncai_not-a-real-key").GetAsync("/netcoreai/api/agents", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task No_key_at_all_is_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.GetTestClient().GetAsync("/netcoreai/api/agents", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_key_stops_working()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);
        await Keys.UpdateAsync(created.Key with { Enabled = false }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientWith(created.Secret).GetAsync("/netcoreai/api/agents", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_expired_key_stops_working()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);
        await Keys.UpdateAsync(created.Key with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientWith(created.Secret).GetAsync("/netcoreai/api/agents", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_key_is_rate_limited_per_minute()
    {
        var created = await Keys.CreateAsync(
            new ApiKey { Id = "", Name = "Slow", Hash = "", Prefix = "", AgentIds = [ApiKey.All], RateLimitPerMinute = 2 },
            Ct);

        var client = ClientWith(created.Secret);
        Assert.True((await client.GetAsync("/netcoreai/api/agents", Ct)).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/netcoreai/api/agents", Ct)).IsSuccessStatusCode);

        var third = await client.GetAsync("/netcoreai/api/agents", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, third.StatusCode);

        // Told when to come back, so a client stops hammering.
        Assert.NotNull(third.Headers.RetryAfter);
    }

    [Theory]
    [InlineData(new[] { "10.0.0.1" }, false)]
    [InlineData(new[] { "127.0.0.1" }, true)]
    [InlineData(new[] { "127.0.0.0/8" }, true)]
    [InlineData(new string[0], true)]
    public void An_allow_list_decides_where_a_key_may_be_used_from(string[] allowed, bool expected)
    {
        var key = new ApiKey { Id = "k", Name = "k", Hash = "", Prefix = "", IpAllowList = allowed };

        Assert.Equal(expected, ApiKeyService.AddressAllowed(key, IPAddress.Parse("127.0.0.1")));
    }

    [Fact]
    public void An_allow_list_with_no_address_to_check_fails_closed()
    {
        // An allow-list that stops applying when the address cannot be read is not an allow-list.
        var key = new ApiKey { Id = "k", Name = "k", Hash = "", Prefix = "", IpAllowList = ["10.0.0.1"] };

        Assert.False(ApiKeyService.AddressAllowed(key, null));
    }

    // ---------- what an accepted key may reach ----------

    [Fact]
    public async Task A_key_can_only_run_the_agents_it_is_scoped_to()
    {
        var created = await KeyAsync(agentIds: "helper");
        var client = ClientWith(created.Secret);

        var allowed = await client.PostAsJsonAsync("/netcoreai/api/agents/helper/run", new { message = "hi" }, Ct);
        var refused = await client.PostAsJsonAsync("/netcoreai/api/agents/other/run", new { message = "hi" }, Ct);

        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("not scoped", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_scoped_to_nothing_can_read_but_not_run()
    {
        var created = await KeyAsync();
        var client = ClientWith(created.Secret);

        // "Nobody said what this may do" reads safely as "nothing", not as "everything".
        Assert.True((await client.GetAsync("/netcoreai/api/agents", Ct)).IsSuccessStatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/netcoreai/api/agents/helper/run", new { message = "hi" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Scope_is_checked_before_a_stream_starts()
    {
        var created = await KeyAsync(agentIds: "helper");

        var response = await ClientWith(created.Secret)
            .PostAsJsonAsync("/netcoreai/api/agents/other/run/stream", new { message = "hi" }, Ct);

        // A refusal written as an SSE event would read to a client as an answer that happened to fail,
        // rather than as a call that was never made.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("event:", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_keys_claims_become_the_identity_a_run_acts_as()
    {
        await _app.Services.GetRequiredService<IAgentService>().SaveAsync(
            new AgentDefinition { Id = "finance", Name = "Finance", Model = "default", AclTags = ["role:finance"] },
            Ct);

        var withRole = await Keys.CreateAsync(
            new ApiKey { Id = "", Name = "Finance key", Hash = "", Prefix = "", AgentIds = [ApiKey.All], Claims = ["role=finance"] },
            Ct);

        var without = await KeyAsync(agentIds: ApiKey.All);

        var allowed = await ClientWith(withRole.Secret).PostAsJsonAsync("/netcoreai/api/agents/finance/run", new { message = "hi" }, Ct);
        var refused = await ClientWith(without.Secret).PostAsJsonAsync("/netcoreai/api/agents/finance/run", new { message = "hi" }, Ct);

        // A key's claims are a service identity: they decide what it may reach, exactly as a person's would.
        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync(Ct));
        Assert.False(refused.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_run_records_which_key_caused_it()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);
        var client = ClientWith(created.Secret);

        var run = await (await client.PostAsJsonAsync("/netcoreai/api/agents/helper/run", new { message = "hi" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);

        var trace = await _app.Services.GetRequiredService<IAgentService>().GetRunAsync(run.GetProperty("runId").GetString()!, Ct);
        Assert.Equal(created.Key.Id, trace!.UserId);
    }

    // ---------- through the API, as the dashboard does it ----------

    [Fact]
    public async Task A_key_can_be_created_through_the_api_without_sending_a_hash()
    {
        var admin = await KeyAsync(agentIds: ApiKey.All);

        // The dashboard sends only what a caller may decide. Binding the endpoint to the stored record
        // instead asked for a hash and a prefix — required members the caller cannot know — which made
        // the endpoint impossible to call at all, and every test that used the service directly missed it.
        var response = await ClientWith(admin.Secret).PostAsJsonAsync(
            "/netcoreai/api/keys",
            new { name = "Orders portal", agentIds = new[] { "helper" }, claims = new[] { "role=support" }, rateLimitPerMinute = 60 },
            Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.StartsWith("ncai_", body.GetProperty("secret").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Orders portal", body.GetProperty("key").GetProperty("name").GetString());
        Assert.Equal(60, body.GetProperty("key").GetProperty("rateLimitPerMinute").GetInt32());
    }

    [Fact]
    public async Task A_key_created_through_the_api_works_and_respects_its_scope()
    {
        var admin = await KeyAsync(agentIds: ApiKey.All);
        var created = await (await ClientWith(admin.Secret).PostAsJsonAsync(
            "/netcoreai/api/keys",
            new { name = "Scoped", agentIds = new[] { "helper" } },
            Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        var client = ClientWith(created.GetProperty("secret").GetString()!);

        Assert.True((await client.PostAsJsonAsync("/netcoreai/api/agents/helper/run", new { message = "hi" }, Ct)).IsSuccessStatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/netcoreai/api/agents/other/run", new { message = "hi" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_update_through_the_api_cannot_smuggle_in_a_chosen_hash()
    {
        var admin = await KeyAsync(agentIds: ApiKey.All);
        var target = await KeyAsync(agentIds: "helper");

        var response = await ClientWith(admin.Secret).PutAsJsonAsync(
            $"/netcoreai/api/keys/{target.Key.Id}",
            new { name = "Renamed", hash = "0000", prefix = "ncai_evil", agentIds = new[] { "helper" } },
            Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));

        // The name changed; the secret did not, and the old one still works.
        var stored = Assert.Single(await Keys.ListAsync(Ct), k => k.Id == target.Key.Id);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(target.Key.Hash, stored.Hash);
        Assert.True((await ClientWith(target.Secret).GetAsync("/netcoreai/api/agents", Ct)).IsSuccessStatusCode);
    }

    // ---------- the page ----------

    [Fact]
    public async Task The_keys_page_lists_keys_by_prefix_and_never_by_secret()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);

        var html = await ClientWith(created.Secret).GetStringAsync("/netcoreai/keys", Ct);

        Assert.Contains("Test key", html, StringComparison.Ordinal);
        Assert.Contains(created.Key.Prefix, html, StringComparison.Ordinal);

        // The prefix is there so a key can be recognised; the rest of the secret must never be.
        Assert.DoesNotContain(created.Secret, html, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Key.Hash, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_page_says_plainly_what_an_empty_scope_means()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);

        var html = await ClientWith(created.Secret).GetStringAsync("/netcoreai/keys", Ct);

        // "Nothing" rather than a blank cell: a reader scanning the table should not have to guess whether
        // an empty scope means everything or nothing.
        Assert.Contains("nothing", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("everything", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_keys_page_is_in_the_navigation()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);

        Assert.Contains("/netcoreai/keys", await ClientWith(created.Secret).GetStringAsync("/netcoreai", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_key_stops_it_immediately()
    {
        var created = await KeyAsync(agentIds: ApiKey.All);
        var client = ClientWith(created.Secret);
        Assert.True((await client.GetAsync("/netcoreai/api/agents", Ct)).IsSuccessStatusCode);

        await Keys.DeleteAsync(created.Key.Id, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/netcoreai/api/agents", Ct)).StatusCode);
    }
}
