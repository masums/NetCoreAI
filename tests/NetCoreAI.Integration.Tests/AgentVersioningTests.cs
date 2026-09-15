using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Agents;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Providers;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Publishing an agent, and what that changes about which one runs.
/// </summary>
/// <remarks>
/// The failure this exists to prevent: somebody edits a live agent, and finds out in production. After the
/// first publish, editing is safe again — the draft is a draft.
/// </remarks>
public sealed class AgentVersioningTests : IAsyncLifetime
{
    private FakeOpenAIServer _openAI = default!;
    private WebApplication _app = default!;
    private string _dataDir = "";

    private IAgentService Agents => _app.Services.GetRequiredService<IAgentService>();

    public async ValueTask InitializeAsync()
    {
        _openAI = await FakeOpenAIServer.StartAsync();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend();

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();

        var connection = await _app.Services.GetRequiredService<IConnectionManager>().SaveAsync(
            new ProviderConnection { Id = "fake", Name = "Fake", ProviderId = OpenAICompatibleProvider.ProviderId, BaseUrl = _openAI.BaseUrl },
            "sk-test",
            Ct);

        await _app.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "default",
            Name = "Fake chat",
            Format = ModelFormat.Remote,
            ProviderId = OpenAICompatibleProvider.ProviderId,
            ConnectionId = connection.Id,
            RemoteModelId = "fake-chat",
            Capabilities = new ModelCapabilities(ModelCapability.Chat),
        }, Ct);
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

    private Task<AgentDefinition> SaveAsync(string prompt, bool enabled = true) =>
        Agents.SaveAsync(
            new AgentDefinition { Id = "helper", Name = "Helper", Model = "default", SystemPrompt = prompt, Enabled = enabled },
            Ct);

    /// <summary>The system prompt the model was actually sent, which is what "which version ran" means.</summary>
    private async Task<string> RunAndReadPromptAsync()
    {
        await Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, new AgentCaller(), Ct);
        return _openAI.Requests[^1]["messages"]!.AsArray()[0]!["content"]!.ToString();
    }

    // ---------- before anybody publishes ----------

    [Fact]
    public async Task An_agent_nobody_published_runs_as_it_is_edited()
    {
        await SaveAsync("first prompt");
        Assert.Equal("first prompt", await RunAndReadPromptAsync());

        await SaveAsync("second prompt");

        // What every agent did before versioning existed, and what a draft should do.
        Assert.Equal("second prompt", await RunAndReadPromptAsync());
    }

    // ---------- after ----------

    [Fact]
    public async Task Publishing_freezes_what_runs()
    {
        await SaveAsync("published prompt");
        await Agents.PublishAsync("helper", "First release.", Ct);

        await SaveAsync("draft prompt");

        // The edit is saved and is not serving. This is the whole feature.
        Assert.Equal("draft prompt", (await Agents.GetAsync("helper", Ct))!.SystemPrompt);
        Assert.Equal("published prompt", await RunAndReadPromptAsync());
    }

    [Fact]
    public async Task Publishing_again_moves_what_runs_forward()
    {
        await SaveAsync("v1");
        await Agents.PublishAsync("helper", "First.", Ct);
        await SaveAsync("v2");
        await Agents.PublishAsync("helper", "Second.", Ct);

        Assert.Equal("v2", await RunAndReadPromptAsync());
    }

    [Fact]
    public async Task Versions_are_numbered_from_one_and_carry_the_note()
    {
        await SaveAsync("v1");
        await Agents.PublishAsync("helper", "Shipped the refund wording.", Ct);
        await SaveAsync("v2");
        await Agents.PublishAsync("helper", "Fixed a typo.", Ct);

        var versions = await Agents.ListVersionsAsync("helper", Ct);

        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.Equal("Fixed a typo.", versions[0].Note);
        Assert.Equal("Shipped the refund wording.", versions[1].Note);
    }

    // ---------- going back ----------

    [Fact]
    public async Task A_rollback_serves_the_old_definition_again()
    {
        await SaveAsync("the good prompt");
        await Agents.PublishAsync("helper", "Good.", Ct);
        await SaveAsync("the bad prompt");
        await Agents.PublishAsync("helper", "Bad.", Ct);

        Assert.Equal("the bad prompt", await RunAndReadPromptAsync());

        await Agents.RollbackAsync("helper", 1, cancellationToken: Ct);

        Assert.Equal("the good prompt", await RunAndReadPromptAsync());
    }

    [Fact]
    public async Task A_rollback_is_a_new_version_rather_than_a_deletion()
    {
        await SaveAsync("v1");
        await Agents.PublishAsync("helper", "First.", Ct);
        await SaveAsync("v2");
        await Agents.PublishAsync("helper", "Second.", Ct);

        var restored = await Agents.RollbackAsync("helper", 1, cancellationToken: Ct);

        // The history is a record of what happened, not of what somebody would now prefer to have
        // happened — and the rollback is itself a thing that happened.
        Assert.Equal(3, restored.Version);
        Assert.Equal(1, restored.RolledBackFrom);
        Assert.Equal(3, (await Agents.ListVersionsAsync("helper", Ct)).Count);
    }

    [Fact]
    public async Task A_rollback_brings_the_draft_with_it()
    {
        await SaveAsync("the good prompt");
        await Agents.PublishAsync("helper", "Good.", Ct);
        await SaveAsync("the bad prompt");
        await Agents.PublishAsync("helper", "Bad.", Ct);

        await Agents.RollbackAsync("helper", 1, cancellationToken: Ct);

        // Somebody reaching for rollback wants the editor to show what is now serving, not the change
        // they just undid.
        Assert.Equal("the good prompt", (await Agents.GetAsync("helper", Ct))!.SystemPrompt);
    }

    [Fact]
    public async Task Rolling_back_to_a_version_that_does_not_exist_says_so()
    {
        await SaveAsync("v1");
        await Agents.PublishAsync("helper", "First.", Ct);

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() => Agents.RollbackAsync("helper", 7, cancellationToken: Ct));

        Assert.Contains("version 7", error.Message, StringComparison.Ordinal);
    }

    // ---------- the operational exceptions ----------

    [Fact]
    public async Task Switching_a_published_agent_off_does_not_need_a_publish()
    {
        await SaveAsync("published prompt");
        await Agents.PublishAsync("helper", "Live.", Ct);

        await SaveAsync("published prompt", enabled: false);

        // Having to publish in order to stop something is the wrong way round in an incident.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, new AgentCaller(), Ct));

        Assert.Contains("turned off", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_published_version_that_has_gone_missing_is_refused_rather_than_served_from_the_draft()
    {
        await SaveAsync("published prompt");
        await Agents.PublishAsync("helper", "Live.", Ct);
        await SaveAsync("draft prompt");

        await _app.Services.GetRequiredService<IMetadataStore>().AgentVersions.DeleteAllAsync("helper", Ct);

        // Silently serving the draft is the one thing publishing promised would not happen.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, new AgentCaller(), Ct));

        Assert.Contains("missing from its history", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_an_agent_takes_its_history_with_it()
    {
        await SaveAsync("v1");
        await Agents.PublishAsync("helper", "First.", Ct);
        await Agents.DeleteAsync("helper", Ct);

        // Otherwise a new agent reusing the id inherits a stranger's past — and, worse, starts published.
        Assert.Empty(await Agents.ListVersionsAsync("helper", Ct));

        await SaveAsync("brand new");
        Assert.Null((await Agents.GetAsync("helper", Ct))!.PublishedVersion);
    }
}
