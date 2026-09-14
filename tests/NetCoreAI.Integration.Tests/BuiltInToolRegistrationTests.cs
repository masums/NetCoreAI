using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Tools;
using NetCoreAI.Tools.BuiltIn;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Which built-in tools a host actually gets. The two that reach outside the process must not exist until
/// somebody has said where they may point.
/// </summary>
public sealed class BuiltInToolRegistrationTests : IAsyncLifetime
{
    private readonly List<WebApplication> _apps = [];
    private readonly List<string> _dataDirs = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        foreach (var dir in _dataDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IReadOnlyList<string>> ToolNamesAsync(Action<BuiltInToolOptions>? configure)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        _dataDirs.Add(dataDir);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(dataDir, "netcoreai.db")};Pooling=False")
            .AddBuiltInTools(configure);

        var app = builder.Build();
        _apps.Add(app);
        app.MapNetCoreAI();
        await app.StartAsync();

        return [.. (await app.Services.GetRequiredService<IToolService>().ListAsync(Ct)).Select(t => t.Name)];
    }

    [Fact]
    public async Task The_harmless_tools_are_there_by_default()
    {
        var names = await ToolNamesAsync(null);

        Assert.Contains("current_date_time", names);
        Assert.Contains("calculate", names);
        Assert.Contains("search_documents", names);
    }

    [Fact]
    public async Task Without_an_allow_list_there_is_no_fetch_tool_at_all()
    {
        var names = await ToolNamesAsync(null);

        // Not "present but always refuses": a model told about a tool will try it, spend a call, and read
        // the refusal as a fault. A tool that does not exist is never attempted.
        Assert.DoesNotContain("fetch_url", names);
    }

    [Fact]
    public async Task Without_a_connection_there_is_no_sql_tool_at_all()
    {
        Assert.DoesNotContain("query_database", await ToolNamesAsync(null));
    }

    [Fact]
    public async Task Naming_a_host_registers_the_fetch_tool()
    {
        var names = await ToolNamesAsync(o => o.FetchAllowedHosts.Add("docs.example.com"));

        Assert.Contains("fetch_url", names);
    }

    [Fact]
    public async Task Giving_a_connection_registers_the_sql_tool()
    {
        var names = await ToolNamesAsync(o => o.Sql = new SqlToolConnection("Microsoft.Data.Sqlite", "Data Source=:memory:"));

        Assert.Contains("query_database", names);
    }

    [Fact]
    public async Task A_host_can_turn_off_what_it_does_not_want()
    {
        var names = await ToolNamesAsync(o =>
        {
            o.Calculator = false;
            o.KnowledgeSearch = false;
        });

        Assert.DoesNotContain("calculate", names);
        Assert.DoesNotContain("search_documents", names);
        Assert.Contains("current_date_time", names);
    }

    [Fact]
    public async Task A_built_in_tool_is_callable_like_any_other()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        _dataDirs.Add(dataDir);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(dataDir, "netcoreai.db")};Pooling=False")
            .AddBuiltInTools();

        var app = builder.Build();
        _apps.Add(app);
        app.MapNetCoreAI();
        await app.StartAsync();

        var function = Assert.Single(await app.Services.GetRequiredService<IToolRegistry>()
            .GetFunctionsAsync(["calculate"], new ToolCallContext(), Ct));

        var result = await function.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { ["expression"] = "(1200 * 1.2) / 3" },
            Ct);

        Assert.Contains("480", result?.ToString() ?? "", StringComparison.Ordinal);
    }
}
