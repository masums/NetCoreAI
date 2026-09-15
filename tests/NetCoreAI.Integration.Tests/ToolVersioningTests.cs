using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Tool groups, version numbers and deprecation.
/// </summary>
/// <remarks>
/// The problem underneath all three: a tool is not owned by whoever is editing it. Changing what the model
/// sees changes what every agent using it does, and deleting one silently removes a capability from agents
/// that will then answer from memory instead of saying they cannot find out.
/// </remarks>
public sealed class ToolVersioningTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    private IToolService Tools => _app.Services.GetRequiredService<IToolService>();

    private IMetadataStore Store => _app.Services.GetRequiredService<IMetadataStore>();

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

    private Task<ToolDefinition> SaveAsync(
        string description = "Look one up.",
        string name = "lookup_order",
        int timeout = 30,
        string parameterName = "id") =>
        Tools.SaveAsync(
            new ToolDefinition
            {
                Id = "t1",
                Name = name,
                Description = description,
                Route = "/api/orders/{id}",
                TimeoutSeconds = timeout,
                Parameters = [new ToolParameter { Name = parameterName, Location = ParameterLocation.Route, Binding = ParameterBinding.Model }],
            },
            cancellationToken: Ct);

    // ---------- versions ----------

    [Fact]
    public async Task A_new_tool_starts_at_version_one()
    {
        Assert.Equal(1, (await SaveAsync()).Version);
    }

    [Theory]
    [InlineData("A different description.", "lookup_order", "id")]
    [InlineData("Look one up.", "find_order", "id")]
    [InlineData("Look one up.", "lookup_order", "order_id")]
    public async Task Changing_what_the_model_sees_is_a_new_version(string description, string name, string parameter)
    {
        await SaveAsync();

        Assert.Equal(2, (await SaveAsync(description, name, parameterName: parameter)).Version);
    }

    [Fact]
    public async Task Changing_something_the_model_cannot_see_is_not()
    {
        await SaveAsync();

        // A changed timeout alters nothing about the calls this tool will receive. Bumping the number for
        // it would make the number meaningless, and the number is only worth having if it means something.
        Assert.Equal(1, (await SaveAsync(timeout: 120)).Version);
    }

    [Fact]
    public async Task Saving_the_same_thing_twice_does_not_move_the_version()
    {
        await SaveAsync();
        await SaveAsync();

        Assert.Equal(1, (await SaveAsync()).Version);
    }

    // ---------- who else this changes ----------

    [Fact]
    public async Task A_tool_knows_which_agents_use_it()
    {
        await SaveAsync();
        await Store.Agents.UpsertAsync(new AgentDefinition { Id = "support", Name = "Support", ToolIds = ["t1"] }, Ct);
        await Store.Agents.UpsertAsync(new AgentDefinition { Id = "other", Name = "Other" }, Ct);

        Assert.Equal("support", Assert.Single(await Tools.UsedByAsync("t1", Ct)));
    }

    [Fact]
    public async Task An_agent_that_reaches_a_tool_through_a_group_counts_as_using_it()
    {
        await SaveAsync();
        await Tools.SaveGroupAsync(new ToolGroup { Id = "orders", Name = "Orders", ToolIds = ["t1"] }, Ct);
        await Store.Agents.UpsertAsync(new AgentDefinition { Id = "support", Name = "Support", ToolIds = ["orders"] }, Ct);

        // Exactly the agent somebody editing the tool would otherwise miss.
        Assert.Equal("support", Assert.Single(await Tools.UsedByAsync("t1", Ct)));
    }

    [Fact]
    public async Task A_tool_can_be_found_by_name_as_well_as_by_id()
    {
        await SaveAsync();
        await Store.Agents.UpsertAsync(new AgentDefinition { Id = "support", Name = "Support", ToolIds = ["lookup_order"] }, Ct);

        Assert.Single(await Tools.UsedByAsync("lookup_order", Ct));
    }

    // ---------- groups ----------

    [Fact]
    public async Task An_agent_naming_a_group_gets_the_tools_in_it()
    {
        await SaveAsync();
        await Tools.SaveGroupAsync(new ToolGroup { Id = "orders", Name = "Orders", ToolIds = ["t1"] }, Ct);

        var functions = await _app.Services.GetRequiredService<IToolRegistry>()
            .GetFunctionsAsync(["orders"], new ToolCallContext(), Ct);

        Assert.Equal("lookup_order", Assert.Single(functions).Name);
    }

    [Fact]
    public async Task Adding_a_tool_to_a_group_gives_it_to_every_agent_that_named_the_group()
    {
        await SaveAsync();
        await Tools.SaveGroupAsync(new ToolGroup { Id = "orders", Name = "Orders", ToolIds = ["t1"] }, Ct);

        await Tools.SaveAsync(
            new ToolDefinition { Id = "t2", Name = "cancel_order", Route = "/api/orders/{id}/cancel" },
            cancellationToken: Ct);
        await Tools.SaveGroupAsync(new ToolGroup { Id = "orders", Name = "Orders", ToolIds = ["t1", "t2"] }, Ct);

        // The point of groups, and the reason expansion happens at run time rather than when the agent is
        // saved: a flattened list stored on the agent would not have changed.
        var functions = await _app.Services.GetRequiredService<IToolRegistry>()
            .GetFunctionsAsync(["orders"], new ToolCallContext(), Ct);

        Assert.Equal(2, functions.Count);
    }

    [Fact]
    public async Task A_group_naming_a_tool_that_has_gone_is_ignored_rather_than_fatal()
    {
        await Tools.SaveGroupAsync(new ToolGroup { Id = "orders", Name = "Orders", ToolIds = ["t1", "deleted"] }, Ct);
        await SaveAsync();

        var functions = await _app.Services.GetRequiredService<IToolRegistry>()
            .GetFunctionsAsync(["orders"], new ToolCallContext(), Ct);

        Assert.Single(functions);
    }

    [Fact]
    public async Task A_group_id_that_is_not_usable_is_refused()
    {
        await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.SaveGroupAsync(new ToolGroup { Id = "not a group", Name = "Bad" }, Ct));
    }

    // ---------- deprecation ----------

    [Fact]
    public async Task A_deprecated_tool_still_works()
    {
        await Tools.SaveAsync(
            new ToolDefinition
            {
                Id = "t1",
                Name = "lookup_order",
                Route = "/api/orders/{id}",
                Deprecated = true,
                DeprecationMessage = "Use the v2 order service.",
                ReplacedBy = "lookup_order_v2",
            },
            cancellationToken: Ct);

        // Removing a capability from a running agent mid-flight is worse than letting it use an old tool
        // for another day: an agent that has quietly lost a tool answers from memory instead of saying it
        // cannot find out.
        var functions = await _app.Services.GetRequiredService<IToolRegistry>()
            .GetFunctionsAsync(["t1"], new ToolCallContext(), Ct);

        Assert.Single(functions);
    }

    [Fact]
    public async Task Deprecation_is_recorded_on_the_tool_for_the_designer_to_show()
    {
        await Tools.SaveAsync(
            new ToolDefinition
            {
                Id = "t1",
                Name = "lookup_order",
                Route = "/x",
                Deprecated = true,
                DeprecationMessage = "Use the v2 order service.",
                ReplacedBy = "lookup_order_v2",
            },
            cancellationToken: Ct);

        var tool = await Store.Tools.GetAsync("t1", Ct);

        Assert.True(tool!.Deprecated);
        Assert.Equal("lookup_order_v2", tool.ReplacedBy);
    }
}
