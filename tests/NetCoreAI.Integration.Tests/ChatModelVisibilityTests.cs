using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// What the chat playground does with a registered model it cannot chat with.
/// </summary>
/// <remarks>
/// Hiding a model that cannot chat is right; hiding it silently is not. Someone who downloads an
/// embedding model, opens the playground and does not find it there concludes the download failed. The
/// model is fine — it answers no questions because that is not what it does — and the selector is the one
/// place they will go looking to find that out.
/// </remarks>
public sealed class ChatModelVisibilityTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

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

        var registry = _app.Services.GetRequiredService<IModelRegistry>();

        await registry.RegisterAsync(new ModelDescriptor
        {
            Id = "talker",
            Name = "A Chat Model",
            Format = ModelFormat.Gguf,
            ProviderId = "gguf",
            Capabilities = new ModelCapabilities(ModelCapability.Chat),
        }, Ct);

        await registry.RegisterAsync(new ModelDescriptor
        {
            Id = "embedder",
            Name = "Nomic Embed Text v1.5",
            Format = ModelFormat.Gguf,
            ProviderId = "gguf",
            Capabilities = new ModelCapabilities(ModelCapability.Embeddings, EmbeddingDimensions: 768),
        }, Ct);

        // Capabilities that were never detected. Unknown is not the same as unable.
        await registry.RegisterAsync(new ModelDescriptor
        {
            Id = "mystery",
            Name = "Undetected Model",
            Format = ModelFormat.Gguf,
            ProviderId = "gguf",
        }, Ct);
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

    private Task<string> ChatPageAsync() => _app.GetTestClient().GetStringAsync("/netcoreai/chat", Ct);

    [Fact]
    public async Task An_embedding_model_is_named_in_the_selector_rather_than_left_out_of_it()
    {
        var html = await ChatPageAsync();

        Assert.Contains("Nomic Embed Text v1.5", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task And_it_says_why_it_cannot_be_picked()
    {
        var html = await ChatPageAsync();

        // Naming it without saying why would read as a bug rather than as an explanation.
        Assert.Contains("an embedding model, used by knowledge bases", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_cannot_actually_be_chosen()
    {
        var html = await ChatPageAsync();

        // Visible and disabled. Selectable would mean every message to it failed at generation time.
        var at = html.IndexOf("Nomic Embed Text v1.5", StringComparison.Ordinal);
        var optionStart = html.LastIndexOf("<option", at, StringComparison.Ordinal);
        Assert.Contains("disabled", html[optionStart..at], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_can_chat_is_still_offered_normally()
    {
        var html = await ChatPageAsync();

        var at = html.IndexOf("A Chat Model", StringComparison.Ordinal);
        var optionStart = html.LastIndexOf("<option", at, StringComparison.Ordinal);
        Assert.Contains("value=\"talker\"", html[optionStart..at], StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", html[optionStart..at], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_whose_capabilities_were_never_detected_is_offered_rather_than_disabled()
    {
        var html = await ChatPageAsync();

        // Refusing to show it would strand a model that works. If it turns out it cannot chat, the
        // failure is at generation time with a message, which is better than never appearing at all.
        var at = html.IndexOf("Undetected Model", StringComparison.Ordinal);
        var optionStart = html.LastIndexOf("<option", at, StringComparison.Ordinal);
        Assert.Contains("value=\"mystery\"", html[optionStart..at], StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", html[optionStart..at], StringComparison.Ordinal);
    }
}
