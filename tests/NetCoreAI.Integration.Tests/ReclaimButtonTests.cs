using Microsoft.AspNetCore.Builder;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Reclaiming disk space from the Storage page.
/// </summary>
/// <remarks>
/// Reported as "there should be a button for reclaim". There was one, at the foot of the file table —
/// which with nine files sat below the fold, so the section read as a list of things you could see and
/// not act on. The section toolbar offered only Rescan.
/// </remarks>
public sealed class ReclaimButtonTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    public async ValueTask InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        // A file under models/ that no registered model claims is exactly what the page calls reclaimable.
        var models = Directory.CreateDirectory(Path.Combine(_dataDir, "models"));
        await File.WriteAllTextAsync(Path.Combine(models.FullName, "leftover.gguf"), new string('x', 2048));

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

    private Task<string> PageAsync() =>
        _app.GetTestClient().GetStringAsync("/netcoreai/storage", TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_reclaim_action_sits_in_the_section_heading_not_only_under_the_table()
    {
        var html = await PageAsync();

        var heading = html.IndexOf("Reclaimable files", StringComparison.Ordinal);
        var table = html.IndexOf("<table id=\"orphans-table\"", StringComparison.Ordinal);
        var button = html.IndexOf("data-reclaim", StringComparison.Ordinal);

        Assert.True(table > 0, "the file table should be on the page");

        // Above the table, so it is visible without scrolling past every file.
        Assert.InRange(button, heading, table);
    }

    [Fact]
    public async Task It_starts_disabled_because_nothing_is_selected_yet()
    {
        var html = await PageAsync();

        // Pressing it with nothing ticked used to produce a toast telling you to tick something. A
        // control that cannot do anything yet should look like it.
        var button = html[html.IndexOf("data-reclaim", StringComparison.Ordinal)..];
        Assert.Contains("disabled", button[..40], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_file_carries_its_size_so_the_button_can_say_what_it_would_free()
    {
        var html = await PageAsync();

        // "Reclaim" is a question about how much space. Without the sizes on the rows the answer needs
        // another request, or a scroll back to the summary at the top.
        Assert.Contains("data-size=\"2048\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_no_model_claims_is_actually_offered()
    {
        Assert.Contains("leftover.gguf", await PageAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_script_url_carries_a_version_that_changes_between_builds()
    {
        var html = await PageAsync();

        // This stamp is the only thing stopping a browser reusing a cached app.js after an upgrade. It
        // used to be the assembly version, which MinVer pins to the major release — 0.0.0 for every
        // pre-1.0 build — so it never changed and the cache never cleared. Found by shipping a change to
        // app.js and watching the browser keep running the old one.
        var expected = typeof(NetCoreAI.Dashboard.Rendering.EmbeddedAssets).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

        Assert.Contains($"app.js?v={expected}", html, StringComparison.Ordinal);
        Assert.Contains($"app.css?v={expected}", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task And_that_version_is_a_real_one_rather_than_the_pinned_assembly_version()
    {
        var html = await PageAsync();

        // The specific value that made the stamp useless.
        Assert.DoesNotContain("app.js?v=0.0.0", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_it_frees_the_space_and_removes_the_file()
    {
        var path = Path.Combine(_dataDir, "models", "leftover.gguf");

        var response = await _app.GetTestClient().PostAsJsonAsync(
            "/netcoreai/api/storage/orphans/delete",
            new { paths = new[] { path } },
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(2048, body.GetProperty("freedBytes").GetInt64());
        Assert.False(File.Exists(path));
    }
}
