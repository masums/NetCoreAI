using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Agents;
using NetCoreAI.Knowledge;
using NetCoreAI.Security;
using NetCoreAI.Tenancy;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Serving more than one customer from one host.
/// </summary>
/// <remarks>
/// Almost every test here is about something <em>not</em> being visible. That is the only property of this
/// feature that matters: everything else is convenience, and one caller reading another's data is the
/// failure nobody recovers from.
/// </remarks>
public sealed class TenancyTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    private IMetadataStore Store => _app.Services.GetRequiredService<IMetadataStore>();

    private ITenantAccessor Tenants => _app.Services.GetRequiredService<ITenantAccessor>();

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
            o.Tenancy.Enabled = true;
            o.Tenancy.Header = "X-Tenant";
            o.Tenancy.CreateOnFirstUse = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, StubAuth>("test", _ => { });

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

    private HttpClient Client(string? tenant)
    {
        var client = _app.GetTestClient();
        if (tenant is { Length: > 0 })
        {
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        }

        return client;
    }

    private async Task SaveAgentAsync(string tenant, string id, string name)
    {
        using var _ = Tenants.Use(tenant);
        await _app.Services.GetRequiredService<IAgentService>()
            .SaveAsync(new AgentDefinition { Id = id, Name = name }, Ct);
    }

    private async Task<IReadOnlyList<AgentDefinition>> AgentsAsync(string tenant)
    {
        using var _ = Tenants.Use(tenant);
        return await _app.Services.GetRequiredService<IAgentService>().ListAsync(Ct);
    }

    // ---------- isolation ----------

    [Fact]
    public async Task One_tenants_agents_are_invisible_to_another()
    {
        await SaveAgentAsync("acme", "support", "Acme support");
        await SaveAgentAsync("globex", "support", "Globex support");

        // The same id in both, which is the case a shared table gets wrong quietly.
        Assert.Equal("Acme support", Assert.Single(await AgentsAsync("acme")).Name);
        Assert.Equal("Globex support", Assert.Single(await AgentsAsync("globex")).Name);
    }

    [Fact]
    public async Task Fetching_another_tenants_thing_by_id_finds_nothing()
    {
        await SaveAgentAsync("acme", "secret-agent", "Acme's");

        using var _ = Tenants.Use("globex");
        Assert.Null(await _app.Services.GetRequiredService<IAgentService>().GetAsync("secret-agent", Ct));
    }

    [Fact]
    public async Task Deleting_by_an_id_you_do_not_own_deletes_nothing()
    {
        await SaveAgentAsync("acme", "target", "Acme's");

        using (var _ = Tenants.Use("globex"))
        {
            await _app.Services.GetRequiredService<IAgentService>().DeleteAsync("target", Ct);
        }

        // Knowing an id must not be enough. This is the test that fails if a store ever bypasses the
        // query filter with a key lookup.
        Assert.Single(await AgentsAsync("acme"));
    }

    [Fact]
    public async Task Knowledge_bases_api_keys_and_audit_entries_are_separated_too()
    {
        using (var _ = Tenants.Use("acme"))
        {
            await _app.Services.GetRequiredService<IKnowledgeService>().CreateAsync(new KnowledgeBase { Id = "docs", Name = "Acme docs" }, Ct);
            await _app.Services.GetRequiredService<IApiKeyService>().CreateAsync(new ApiKey { Id = "k", Name = "Acme key", Hash = "h-acme", Prefix = "p" }, Ct);
        }

        using (var _ = Tenants.Use("globex"))
        {
            Assert.Empty(await _app.Services.GetRequiredService<IKnowledgeService>().ListAsync(Ct));
            Assert.Empty(await _app.Services.GetRequiredService<IApiKeyService>().ListAsync(Ct));
            Assert.Empty(await _app.Services.GetRequiredService<IAuditLog>().ListAsync(new AuditFilter(), Ct));
        }

        using (var _ = Tenants.Use("acme"))
        {
            Assert.NotEmpty(await _app.Services.GetRequiredService<IAuditLog>().ListAsync(new AuditFilter(), Ct));
        }
    }

    [Fact]
    public async Task An_api_key_cannot_be_used_to_reach_another_tenant()
    {
        using (var _ = Tenants.Use("acme"))
        {
            await _app.Services.GetRequiredService<IApiKeyService>()
                .CreateAsync(new ApiKey { Id = "k", Name = "Acme", Hash = "shared-hash", Prefix = "p" }, Ct);
        }

        // Keys are looked up by hash on every authenticated call, which is a lookup that does not go
        // through an id. It must still be scoped, or one tenant's credential authenticates in another.
        using (var _ = Tenants.Use("globex"))
        {
            Assert.Null(await Store.ApiKeys.FindByHashAsync("shared-hash", Ct));
        }
    }

    // ---------- resolving ----------

    [Fact]
    public async Task The_tenant_comes_from_the_request()
    {
        await Client("acme").PostAsJsonAsync("/netcoreai/api/agents", new { id = "a", name = "Acme's", model = "default" }, Ct);

        Assert.Single(await AgentsAsync("acme"));
        Assert.Empty(await AgentsAsync("globex"));
    }

    [Fact]
    public async Task A_request_naming_no_tenant_is_refused_rather_than_served_as_the_default()
    {
        // The failure that matters. Falling back to a default here would quietly hand an unidentified
        // caller somebody's data.
        var response = await Client(null).GetAsync("/netcoreai/api/agents", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_tenant_id_that_could_escape_a_path_is_refused()
    {
        // Tenant ids reach storage paths and vector collection names.
        foreach (var bad in (string[])["../other", "a/b", ".hidden", "", new string('x', 65)])
        {
            Assert.False(TenantId.IsValid(bad), bad);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Client("../other").GetAsync("/netcoreai/api/agents", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_tenant_is_refused_and_keeps_its_data()
    {
        var tenants = _app.Services.GetRequiredService<ITenantService>();
        await tenants.CreateAsync(new Tenant { Id = "acme", Name = "Acme" }, Ct);
        await SaveAgentAsync("acme", "kept", "Still here");

        await tenants.UpdateAsync((await tenants.GetAsync("acme", Ct))! with { Enabled = false }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await Client("acme").GetAsync("/netcoreai/api/agents", Ct)).StatusCode);

        // Disabling is usually a dispute, not a deletion.
        await tenants.UpdateAsync((await tenants.GetAsync("acme", Ct))! with { Enabled = true }, Ct);
        Assert.Single(await AgentsAsync("acme"));
    }

    [Fact]
    public async Task The_scope_is_put_back_when_it_ends()
    {
        Assert.Equal(TenantId.Default, Tenants.Current);

        using (var outer = Tenants.Use("acme"))
        {
            Assert.Equal("acme", Tenants.Current);
            using (var inner = Tenants.Use("globex"))
            {
                Assert.Equal("globex", Tenants.Current);
            }

            // Nested scopes restore what they replaced, not the default — otherwise an administrator
            // acting on one tenant's behalf inside another's request would leak back the wrong way.
            Assert.Equal("acme", Tenants.Current);
        }

        Assert.Equal(TenantId.Default, Tenants.Current);
        await Task.CompletedTask;
    }

    // ---------- storage ----------

    [Fact]
    public async Task Two_tenants_with_the_same_knowledge_base_id_do_not_share_a_vector_collection()
    {
        var knowledge = _app.Services.GetRequiredService<IKnowledgeService>();

        using (var _ = Tenants.Use("acme"))
        {
            await knowledge.CreateAsync(new KnowledgeBase { Id = "docs", Name = "Acme docs" }, Ct);
        }

        using (var _ = Tenants.Use("globex"))
        {
            await knowledge.CreateAsync(new KnowledgeBase { Id = "docs", Name = "Globex docs" }, Ct);
        }

        string Collection(string tenant)
        {
            using var _ = Tenants.Use(tenant);
            return knowledge.ListAsync(Ct).GetAwaiter().GetResult()[0].Collection;
        }

        // A shared collection is the same leak as a shared table, one layer down: retrieval would hand
        // one tenant passages out of the other's documents.
        Assert.NotEqual(Collection("acme"), Collection("globex"));
    }

    [Fact]
    public async Task The_default_tenant_keeps_the_collection_name_it_already_had()
    {
        using var _ = Tenants.Use(TenantId.Default);
        var created = await _app.Services.GetRequiredService<IKnowledgeService>()
            .CreateAsync(new KnowledgeBase { Id = "docs", Name = "Docs" }, Ct);

        // Switching tenancy on must not orphan the vectors a single-tenant host already indexed.
        Assert.Equal("kb_docs", created.Collection);
    }

    [Fact]
    public void Upload_folders_are_separated_and_the_default_tenant_keeps_its_path()
    {
        var shared = NetCoreAI.Knowledge.FileDataSource.UploadFolder("/data", "docs");
        var acme = NetCoreAI.Knowledge.FileDataSource.UploadFolder("/data", "docs", "acme");

        Assert.Equal(shared, NetCoreAI.Knowledge.FileDataSource.UploadFolder("/data", "docs", TenantId.Default));
        Assert.NotEqual(shared, acme);
        Assert.Contains("acme", acme, StringComparison.Ordinal);
    }

    // ---------- quotas ----------

    private async Task SetQuotaAsync(string tenant, TenantQuota quota)
    {
        var tenants = _app.Services.GetRequiredService<ITenantService>();
        var existing = await tenants.GetAsync(tenant, Ct) ?? await tenants.CreateAsync(new Tenant { Id = tenant, Name = tenant }, Ct);
        await tenants.UpdateAsync(existing with { Quota = quota }, Ct);
    }

    [Fact]
    public async Task A_tenant_at_its_agent_limit_cannot_create_another()
    {
        await SetQuotaAsync("acme", new TenantQuota { MaxAgents = 2 });

        await SaveAgentAsync("acme", "one", "One");
        await SaveAgentAsync("acme", "two", "Two");

        using var _ = Tenants.Use("acme");
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            _app.Services.GetRequiredService<IAgentService>().SaveAsync(new AgentDefinition { Id = "three", Name = "Three" }, Ct));

        // The numbers are in the message, because "quota exceeded" tells somebody nothing about what to
        // delete or what to ask for.
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
        Assert.Contains("agents", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_the_agent_that_reached_the_limit_still_works()
    {
        await SetQuotaAsync("acme", new TenantQuota { MaxAgents = 1 });
        await SaveAgentAsync("acme", "only", "Only");

        // Otherwise the limit is a trap rather than a ceiling: the tenant could never fix a typo in the
        // agent that took them to it.
        await SaveAgentAsync("acme", "only", "Renamed");

        Assert.Equal("Renamed", Assert.Single(await AgentsAsync("acme")).Name);
    }

    [Fact]
    public async Task One_tenants_limit_says_nothing_about_another()
    {
        await SetQuotaAsync("acme", new TenantQuota { MaxAgents = 1 });
        await SaveAgentAsync("acme", "one", "One");
        await SaveAgentAsync("globex", "one", "One");
        await SaveAgentAsync("globex", "two", "Two");

        // Globex has no quota, and Acme's full table is not its problem.
        Assert.Equal(2, (await AgentsAsync("globex")).Count);
    }

    [Fact]
    public async Task A_knowledge_base_limit_is_enforced_too()
    {
        await SetQuotaAsync("acme", new TenantQuota { MaxKnowledgeBases = 1 });

        using var _ = Tenants.Use("acme");
        var knowledge = _app.Services.GetRequiredService<IKnowledgeService>();
        await knowledge.CreateAsync(new KnowledgeBase { Id = "first", Name = "First" }, Ct);

        await Assert.ThrowsAsync<NetCoreAIException>(() =>
            knowledge.CreateAsync(new KnowledgeBase { Id = "second", Name = "Second" }, Ct));
    }

    [Fact]
    public async Task With_no_quota_nothing_is_refused()
    {
        // The default, and the test that says having the feature costs a host nothing.
        for (var i = 0; i < 5; i++)
        {
            await SaveAgentAsync("globex", $"a{i}", $"Agent {i}");
        }

        Assert.Equal(5, (await AgentsAsync("globex")).Count);
    }

    [Fact]
    public async Task Usage_reports_what_is_used_and_what_is_allowed()
    {
        await SetQuotaAsync("acme", new TenantQuota { MaxAgents = 3 });
        await SaveAgentAsync("acme", "one", "One");

        using var _ = Tenants.Use("acme");
        var usage = await _app.Services.GetRequiredService<ITenantQuotas>().UsageAsync(Ct);

        var agents = Assert.Single(usage, u => u.Name == "agents");
        Assert.Equal(1, agents.Used);
        Assert.Equal(3, agents.Limit);
        Assert.False(agents.AtLimit);
    }

    [Fact]
    public async Task A_tenant_over_its_daily_tokens_cannot_start_another_run()
    {
        await SetQuotaAsync("acme", new TenantQuota { MaxTokensPerDay = 100 });

        using var _ = Tenants.Use("acme");
        _app.Services.GetRequiredService<NetCoreAI.Guardrails.IGuardrailService>()
            .RecordUsage("agent", null, null, 150, 0, "acme");

        Assert.NotNull(_app.Services.GetRequiredService<ITenantQuotas>().CheckDailyBudget());
    }

    [Fact]
    public async Task A_daily_budget_is_one_tenants_alone()
    {
        await SetQuotaAsync("acme", new TenantQuota { MaxTokensPerDay = 100 });
        await SetQuotaAsync("globex", new TenantQuota { MaxTokensPerDay = 100 });

        _app.Services.GetRequiredService<NetCoreAI.Guardrails.IGuardrailService>()
            .RecordUsage("agent", null, null, 150, 0, "acme");

        var quotas = _app.Services.GetRequiredService<ITenantQuotas>();

        using (var _ = Tenants.Use("acme"))
        {
            Assert.NotNull(quotas.CheckDailyBudget());
        }

        using (var _ = Tenants.Use("globex"))
        {
            Assert.Null(quotas.CheckDailyBudget());
        }
    }

    // ---------- what an existing host sees ----------

    [Fact]
    public async Task Data_written_before_tenancy_existed_belongs_to_the_default_tenant()
    {
        // The upgrade story: the TenantId column defaults to "default", so rows written by a version that
        // had never heard of tenants read back as one tenant's rather than as nobody's.
        using (var _ = Tenants.Use(TenantId.Default))
        {
            await _app.Services.GetRequiredService<IAgentService>()
                .SaveAsync(new AgentDefinition { Id = "legacy", Name = "From before" }, Ct);
        }

        Assert.Single(await AgentsAsync(TenantId.Default));
    }

    private sealed class StubAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Alice")], "test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}
