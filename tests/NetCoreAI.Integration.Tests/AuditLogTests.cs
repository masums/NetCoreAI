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
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The audit log: what is recorded, who it is attributed to, and what it deliberately does not contain.
/// </summary>
public sealed class AuditLogTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    private IAuditLog Audit => _app.Services.GetRequiredService<IAuditLog>();

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
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        // A signed-in caller for every request, so entries have somebody to be attributed to.
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

    private Task<IReadOnlyList<AuditEntry>> EntriesAsync(string? entityType = null) =>
        Audit.ListAsync(new AuditFilter { EntityType = entityType, Limit = 100 }, Ct);

    // ---------- what is recorded ----------

    [Fact]
    public async Task Creating_and_changing_and_deleting_an_agent_are_all_recorded()
    {
        var agents = _app.Services.GetRequiredService<IAgentService>();
        await agents.SaveAsync(new AgentDefinition { Id = "helper", Name = "Helper" }, Ct);
        await agents.SaveAsync(new AgentDefinition { Id = "helper", Name = "Helper renamed" }, Ct);
        await agents.DeleteAsync("helper", Ct);

        var entries = await EntriesAsync(AuditEntity.Agent);

        Assert.Equal(
            [AuditAction.Deleted, AuditAction.Updated, AuditAction.Created],
            entries.Select(e => e.Action));
    }

    [Fact]
    public async Task A_deleted_thing_keeps_the_name_it_had()
    {
        var agents = _app.Services.GetRequiredService<IAgentService>();
        await agents.SaveAsync(new AgentDefinition { Id = "gone", Name = "Support triage" }, Ct);
        await agents.DeleteAsync("gone", Ct);

        // Half the point of an audit log is explaining something that no longer exists. Looking the name
        // up later is not an option, so it is copied at the time.
        var deleted = Assert.Single(await EntriesAsync(AuditEntity.Agent), e => e.Action == AuditAction.Deleted);
        Assert.Equal("Support triage", deleted.EntityName);
    }

    [Fact]
    public async Task A_knowledge_base_and_an_api_key_are_recorded_too()
    {
        await _app.Services.GetRequiredService<IKnowledgeService>()
            .CreateAsync(new KnowledgeBase { Id = "docs", Name = "Docs" }, Ct);

        await _app.Services.GetRequiredService<IApiKeyService>()
            .CreateAsync(new ApiKey { Id = "k1", Name = "Billing service", Hash = "", Prefix = "" }, Ct);

        Assert.Single(await EntriesAsync(AuditEntity.KnowledgeBase));
        Assert.Single(await EntriesAsync(AuditEntity.ApiKey));
    }

    [Fact]
    public async Task An_api_keys_secret_is_never_in_the_log()
    {
        var created = await _app.Services.GetRequiredService<IApiKeyService>()
            .CreateAsync(new ApiKey { Id = "k1", Name = "Billing", Hash = "", Prefix = "" }, Ct);

        // The log records that a key was issued and which one. It must not record anything that could be
        // used as the key, and neither the secret nor its hash qualifies as "detail".
        var entry = Assert.Single(await EntriesAsync(AuditEntity.ApiKey));
        var text = System.Text.Json.JsonSerializer.Serialize(entry);

        Assert.DoesNotContain(created.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Key.Hash, text, StringComparison.Ordinal);
    }

    // ---------- who did it ----------

    [Fact]
    public async Task An_entry_carries_the_caller_who_caused_it()
    {
        var response = await _app.GetTestClient().PostAsJsonAsync(
            "/netcoreai/api/agents",
            new { id = "viaapi", name = "Via the API", model = "default" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(await EntriesAsync(AuditEntity.Agent));
        Assert.Equal(ActorKind.User, entry.ActorKind);
        Assert.Equal("Alice", entry.ActorName);
        Assert.Equal("alice-id", entry.ActorId);
    }

    [Fact]
    public async Task Work_with_no_request_behind_it_is_recorded_as_nobody_rather_than_guessed_at()
    {
        // Called straight off the service, as a background job would. Inventing an actor here would put a
        // name against something a person did not do, which is worse than an honest blank.
        await _app.Services.GetRequiredService<IAgentService>()
            .SaveAsync(new AgentDefinition { Id = "background", Name = "Background" }, Ct);

        var entry = Assert.Single(await EntriesAsync(AuditEntity.Agent));
        Assert.Equal(ActorKind.Anonymous, entry.ActorKind);
        Assert.Null(entry.ActorId);
    }

    // ---------- reading it back ----------

    [Fact]
    public async Task The_log_can_be_filtered_to_one_thing()
    {
        var agents = _app.Services.GetRequiredService<IAgentService>();
        await agents.SaveAsync(new AgentDefinition { Id = "a", Name = "A" }, Ct);
        await agents.SaveAsync(new AgentDefinition { Id = "b", Name = "B" }, Ct);
        await agents.DeleteAsync("a", Ct);

        var forA = await Audit.ListAsync(new AuditFilter { EntityId = "a" }, Ct);

        Assert.Equal(2, forA.Count);
        Assert.All(forA, e => Assert.Equal("a", e.EntityId));
    }

    [Fact]
    public async Task The_newest_entry_comes_first()
    {
        var agents = _app.Services.GetRequiredService<IAgentService>();
        await agents.SaveAsync(new AgentDefinition { Id = "first", Name = "First" }, Ct);
        await agents.SaveAsync(new AgentDefinition { Id = "second", Name = "Second" }, Ct);

        // What an audit page opens on is "what just happened", not "what happened first".
        Assert.Equal("second", (await EntriesAsync())[0].EntityId);
    }

    [Fact]
    public async Task The_api_returns_it()
    {
        await _app.Services.GetRequiredService<IAgentService>()
            .SaveAsync(new AgentDefinition { Id = "helper", Name = "Helper" }, Ct);

        var entries = await _app.GetTestClient()
            .GetFromJsonAsync<List<AuditEntry>>("/netcoreai/api/audit?entityType=agent", Ct);

        Assert.Equal("helper", Assert.Single(entries!).EntityId);
    }

    // ---------- retention ----------

    [Fact]
    public async Task Retention_removes_what_is_older_than_the_host_keeps()
    {
        var store = _app.Services.GetRequiredService<IMetadataStore>();
        await store.Audit.WriteAsync(
            new AuditEntry
            {
                Id = "old",
                At = DateTimeOffset.UtcNow.AddDays(-400),
                Action = AuditAction.Created,
                EntityType = AuditEntity.Agent,
                EntityId = "ancient",
            },
            Ct);

        await Audit.WriteAsync(AuditAction.Created, AuditEntity.Agent, "recent", "Recent", cancellationToken: Ct);

        var removed = await store.Audit.PruneAsync(DateTimeOffset.UtcNow.AddDays(-365), Ct);

        Assert.Equal(1, removed);
        Assert.Equal("recent", Assert.Single(await EntriesAsync()).EntityId);
    }

    [Fact]
    public async Task Turning_the_log_off_stops_entries_being_written()
    {
        _app.Services.GetRequiredService<IOptionsMonitor<AuditOptions>>().CurrentValue.Enabled = false;
        try
        {
            await _app.Services.GetRequiredService<IAgentService>()
                .SaveAsync(new AgentDefinition { Id = "quiet", Name = "Quiet" }, Ct);

            Assert.Empty(await EntriesAsync());
        }
        finally
        {
            _app.Services.GetRequiredService<IOptionsMonitor<AuditOptions>>().CurrentValue.Enabled = true;
        }
    }

    [Fact]
    public async Task A_store_that_cannot_be_written_to_does_not_fail_the_thing_being_recorded()
    {
        // The trade this makes deliberately: an audit log that can fail a save is one that gets switched
        // off the first time it does. A gap in the log is visible; an outage is not recoverable.
        await AuditLogOverFailingStore.WriteAsync(
            new AuditEntry { Id = "x", Action = AuditAction.Created, EntityType = AuditEntity.Agent },
            Ct);
    }

    private sealed class AuditLogOverFailingStore
    {
        public static Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            var store = new FailingStore();
            var log = new AuditLog(
                store,
                new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
                new StaticOptions(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditLog>.Instance);

            return log.WriteAsync(entry, cancellationToken);
        }
    }

    private sealed class StaticOptions : IOptionsMonitor<AuditOptions>
    {
        public AuditOptions CurrentValue { get; } = new();
        public AuditOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuditOptions, string?> listener) => null;
    }

    /// <summary>An ordinary store whose audit table refuses every call.</summary>
    private sealed class FailingStore : IMetadataStore
    {
        private readonly NetCoreAI.Storage.InMemoryMetadataStore _inner = new();

        public IAuditStore Audit { get; } = new Throwing();

        public IModelStore Models => _inner.Models;
        public IAliasStore Aliases => _inner.Aliases;
        public IProviderConnectionStore Connections => _inner.Connections;
        public IChatSessionStore Sessions => _inner.Sessions;
        public ISettingsStore Settings => _inner.Settings;
        public IDownloadStore Downloads => _inner.Downloads;
        public IKnowledgeStore Knowledge => _inner.Knowledge;
        public IJobStore Jobs => _inner.Jobs;
        public IToolStore Tools => _inner.Tools;
        public IAgentStore Agents => _inner.Agents;
        public IRunStore Runs => _inner.Runs;
        public IApiKeyStore ApiKeys => _inner.ApiKeys;
        public IAgentVersionStore AgentVersions => _inner.AgentVersions;
        public IToolGroupStore ToolGroups => _inner.ToolGroups;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => _inner.IsHealthyAsync(cancellationToken);

        private sealed class Throwing : IAuditStore
        {
            public Task<IReadOnlyList<AuditEntry>> ListAsync(AuditFilter filter, CancellationToken ct = default) =>
                throw new InvalidOperationException("the audit table is unavailable");

            public Task WriteAsync(AuditEntry entry, CancellationToken ct = default) =>
                throw new InvalidOperationException("the audit table is unavailable");

            public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) =>
                throw new InvalidOperationException("the audit table is unavailable");
        }
    }

    private sealed class StubAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "Alice"), new Claim(ClaimTypes.NameIdentifier, "alice-id")],
                "test");

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}
