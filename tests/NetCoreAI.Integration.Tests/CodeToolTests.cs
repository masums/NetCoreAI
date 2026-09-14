using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Tools written as ordinary methods: what the model is shown, that calling one reaches the method, and
/// that the designer cannot edit something the compiler owns.
/// </summary>
public sealed class CodeToolTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    /// <summary>Proves an instance tool gets its dependencies from DI rather than being newed up.</summary>
    private sealed class Rates
    {
        private readonly Dictionary<string, decimal> _rates = new(StringComparer.OrdinalIgnoreCase)
        {
            ["EUR"] = 1.17m,
            ["USD"] = 1.27m,
        };

        public decimal For(string currency) => _rates.GetValueOrDefault(currency);
    }

    private sealed class MoneyTools(Rates rates)
    {
        [AITool("convert_money", Description = "Convert an amount from pounds into another currency.")]
        public decimal Convert(
            [Description("The amount in pounds.")] decimal amount,
            [Description("ISO currency code, such as EUR.")] string currency) =>
            Math.Round(amount * rates.For(currency), 2);

        [AITool(Description = "The currencies this host can convert into.")]
        public static string[] SupportedCurrencies() => ["EUR", "USD"];

        [AITool("drop_everything", Safety = ToolSafety.SideEffecting)]
        public static string DangerousThing() => "done";

        public static string NotATool() => "invisible";
    }

    private sealed class NothingMarked
    {
        public static string Method() => "";
    }

    public async ValueTask InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<Rates>();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")
            .AddAITool<MoneyTools>();

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

    private IToolService Tools => _app.Services.GetRequiredService<IToolService>();

    private async Task<AIFunction> FunctionAsync(string name) =>
        Assert.Single(await _app.Services.GetRequiredService<IToolRegistry>().GetFunctionsAsync([name], new ToolCallContext(), Ct));

    [Fact]
    public async Task A_marked_method_becomes_a_tool_and_an_unmarked_one_does_not()
    {
        var names = (await Tools.ListAsync(Ct)).Select(t => t.Name).ToList();

        Assert.Contains("convert_money", names);
        Assert.Contains("drop_everything", names);
        Assert.DoesNotContain("not_a_tool", names);
    }

    [Fact]
    public async Task A_method_name_becomes_a_readable_tool_name_when_none_was_given()
    {
        // PascalCase reads oddly to a model beside the snake_case names every other tool has.
        Assert.Contains("supported_currencies", (await Tools.ListAsync(Ct)).Select(t => t.Name));
    }

    [Theory]
    [InlineData("Convert", "convert")]
    [InlineData("SupportedCurrencies", "supported_currencies")]
    [InlineData("GetHTTPStatus", "get_http_status")]
    [InlineData("ID", "id")]
    public void Names_are_converted_the_way_a_reader_would_write_them(string method, string expected) =>
        Assert.Equal(expected, CodeToolExtensions.SnakeCase(method));

    [Fact]
    public async Task The_methods_signature_is_the_schema_the_model_is_shown()
    {
        var function = await FunctionAsync("convert_money");
        var properties = function.JsonSchema.GetProperty("properties");

        Assert.Equal("Convert an amount from pounds into another currency.", function.Description);
        Assert.True(properties.TryGetProperty("amount", out var amount));
        Assert.True(properties.TryGetProperty("currency", out _));

        // The parameter descriptions are most of what makes a tool call accurate, so they have to survive.
        Assert.Contains("pounds", amount.GetProperty("description").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Calling_a_code_tool_runs_the_method_with_its_injected_dependencies()
    {
        var function = await FunctionAsync("convert_money");

        var result = await function.InvokeAsync(new AIFunctionArguments { ["amount"] = 100m, ["currency"] = "EUR" }, Ct);

        // 100 x 1.17, from the Rates service DI supplied rather than a new one.
        Assert.Contains("117", result?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_static_method_needs_no_instance()
    {
        var result = await (await FunctionAsync("supported_currencies")).InvokeAsync(new AIFunctionArguments(), Ct);

        Assert.Contains("EUR", result?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_tool_carries_the_safety_its_author_declared()
    {
        var tools = await Tools.ListAsync(Ct);

        Assert.Equal(ToolSafety.ReadOnly, Assert.Single(tools, t => t.Name == "convert_money").Safety);
        Assert.Equal(ToolSafety.SideEffecting, Assert.Single(tools, t => t.Name == "drop_everything").Safety);
    }

    [Fact]
    public async Task A_code_tool_cannot_be_edited_in_the_designer()
    {
        var tool = Assert.Single(await Tools.ListAsync(Ct), t => t.Name == "convert_money");

        // Saving a row for it would create a copy that the next deployment silently disagrees with.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.SaveAsync(tool with { Description = "something else" }, cancellationToken: Ct));

        Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_saved_tool_cannot_take_a_code_tools_name()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.SaveAsync(
                new ToolDefinition { Id = "t1", Name = "convert_money", Kind = ToolKind.Endpoint, Route = "/api/x" },
                cancellationToken: Ct));

        Assert.Contains("already called", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registering_a_type_with_no_marked_method_says_so_rather_than_registering_nothing()
    {
        var services = new ServiceCollection();
        var builder = services.AddNetCoreAI(o => o.DataDirectory = _dataDir);

        // Silently registering nothing leaves someone wondering why their tool never appears.
        var error = Assert.Throws<NetCoreAIException>(() => builder.AddAITool<NothingMarked>());

        Assert.Contains("[AITool]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_tools_id_is_stable_across_restarts()
    {
        var first = Assert.Single(await Tools.ListAsync(Ct), t => t.Name == "convert_money").Id;

        // Derived from where the method lives, not from load order, so an agent that names a code tool
        // keeps naming the same one after a redeploy.
        Assert.StartsWith("code_", first, StringComparison.Ordinal);
        Assert.Equal(first, Assert.Single(await Tools.ListAsync(Ct), t => t.Name == "convert_money").Id);
    }
}
