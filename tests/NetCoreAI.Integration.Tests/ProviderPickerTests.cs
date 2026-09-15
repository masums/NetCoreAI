using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The list somebody picks from when adding a remote provider.
/// </summary>
/// <remarks>
/// Services are named directly rather than hidden behind the package that happens to implement them.
/// Nobody sets out to add "an OpenAI-compatible provider"; they set out to add Gemini, and a name only
/// reachable after guessing the right package reads as a service the framework does not support. The
/// package stays visible as the group each service sits under, because that is what gets stored and what
/// the documentation names.
/// </remarks>
public sealed class ProviderPickerTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    public async ValueTask InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();

        // Deliberately not every backend: Anthropic is left out to prove the list follows what is
        // actually registered.
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddOpenAICompatibleBackend()
            .AddOllamaBackend()
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
        _app.GetTestClient().GetStringAsync("/netcoreai/providers", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Gemini_is_an_option_in_the_list_itself()
    {
        var html = await PageAsync();

        // Asserted on the rendered option, not on the name appearing somewhere in the page: the preset
        // table is also embedded as JSON for the form script, so a bare substring search passes even
        // when nothing is rendered at all. A mutation run caught exactly that.
        Assert.Contains("<option value=\"openai|gemini\">Google Gemini</option>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task So_is_OpenRouter()
    {
        Assert.Contains("<option value=\"openai|openrouter\">OpenRouter</option>", await PageAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_service_carries_the_package_that_handles_it()
    {
        var html = await PageAsync();

        // Both halves travel in the option value, because providerId is what is stored on the connection
        // and what every doc, API response and support question refers to.
        Assert.Contains("<option value=\"openai|gemini\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"openai|openrouter\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_package_is_still_shown_as_the_group_a_service_sits_under()
    {
        var html = await PageAsync();

        // Grouped, not flattened: "OpenAI-compatible" and "Ollama" are different things to install, and
        // a flat list of a dozen names would hide that distinction entirely.
        Assert.Contains("<optgroup label=\"OpenAI-compatible\"", html, StringComparison.Ordinal);
        Assert.Contains("<optgroup label=\"Ollama\"", html, StringComparison.Ordinal);

        // And the group actually contains its services rather than being an empty heading.
        var group = html[html.IndexOf("<optgroup label=\"Ollama\"", StringComparison.Ordinal)..];
        group = group[..group.IndexOf("</optgroup>", StringComparison.Ordinal)];
        Assert.Contains("<option", group, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_that_is_not_registered_offers_nothing()
    {
        var html = await PageAsync();

        // Anthropic was not added to this host. Offering it would repeat the mistake this change fixes,
        // in reverse: a name the UI shows and the host cannot honour.
        Assert.DoesNotContain("<optgroup label=\"Anthropic\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_settings_page_ticks_a_provider_that_is_on()
    {
        var html = await _app.GetTestClient().GetStringAsync("/netcoreai/settings", TestContext.Current.CancellationToken);

        // Checked means on, like every other checkbox on that page. It used to mean the opposite: the
        // boxes named providers to *disable*, so ticking all of them to turn everything on turned
        // everything off — and the Providers page then reported no provider package registered, which
        // reads as a missing NuGet reference rather than a setting. Found by hitting it.
        Assert.Contains("<legend>Enabled providers</legend>", html, StringComparison.Ordinal);
        Assert.Contains("name=\"enabledProviders\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"disabledProviders\"", html, StringComparison.Ordinal);

        // Nothing is disabled on this host, so every box is ticked.
        var fieldset = html[html.IndexOf("data-all-providers", StringComparison.Ordinal)..];
        fieldset = fieldset[..fieldset.IndexOf("</fieldset>", StringComparison.Ordinal)];
        Assert.Equal(
            fieldset.Split("type=\"checkbox\"").Length - 1,
            fieldset.Split("checked").Length - 1);
    }

    [Fact]
    public async Task The_settings_page_lists_every_provider_so_the_unticked_ones_can_be_worked_out()
    {
        var html = await _app.GetTestClient().GetStringAsync("/netcoreai/settings", TestContext.Current.CancellationToken);

        // The form posts which providers are on; the API stores which are off. Without the full list the
        // complement cannot be computed, and unticking a box would silently do nothing.
        Assert.Contains("data-all-providers=\"", html, StringComparison.Ordinal);
        Assert.Contains("openai", html[html.IndexOf("data-all-providers=\"", StringComparison.Ordinal)..][..60], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preset_needing_an_extra_setting_still_declares_it()
    {
        var html = await PageAsync();

        // Azure needs an api-version. Collapsing two dropdowns into one must not lose the field that
        // appears when a particular preset is chosen.
        Assert.Contains("data-setting=\"api-version\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"openai|azure\"", html, StringComparison.Ordinal);
    }
}
