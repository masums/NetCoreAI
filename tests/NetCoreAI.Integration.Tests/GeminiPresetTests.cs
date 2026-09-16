using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Backends.OpenAICompatible;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Google Gemini, reached through its OpenAI-compatible endpoint rather than through a provider of its
/// own.
/// </summary>
/// <remarks>
/// A preset rather than a new backend because Gemini publishes a real OpenAI-shaped API: chat, streaming,
/// model listing and embeddings all answer. Everything below was checked against the live endpoint rather
/// than read off documentation.
/// </remarks>
public sealed class GeminiPresetTests : IAsyncLifetime
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
            .AddOpenAICompatibleBackend()
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

    private IModelProvider Provider => _app.Services.GetServices<IModelProvider>()
        .First(p => p.Id == OpenAICompatibleProvider.ProviderId);

    private ProviderPreset Gemini => ((IConnectionAwareProvider)Provider).Presets.Single(p => p.Id == "gemini");

    private ModelCapabilities ViaConnection(string preset, string remoteModelId) =>
        OpenAICompatibleProvider.ApplyPresetLimits(preset, Capabilities(remoteModelId));

    private ModelCapabilities Capabilities(string remoteModelId) => Provider.GetCapabilities(new ModelDescriptor
    {
        Id = "m",
        Name = remoteModelId,
        Format = ModelFormat.Remote,
        ProviderId = OpenAICompatibleProvider.ProviderId,
        ConnectionId = "c",
        RemoteModelId = remoteModelId,
    });

    [Fact]
    public void The_preset_points_at_the_compatibility_endpoint_rather_than_the_native_one()
    {
        // The native API speaks a different shape entirely; only the /openai suffix takes chat/completions.
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", Gemini.DefaultBaseUrl);
        Assert.True(Gemini.RequiresApiKey);
    }

    [Fact]
    public void It_lists_models_and_does_embeddings()
    {
        Assert.True(Gemini.SupportsModelListing);
        Assert.True(Gemini.SupportsEmbeddings);
    }

    [Fact]
    public void A_gemini_chat_model_does_not_claim_tool_calling()
    {
        var capabilities = Capabilities("gemini-3.6-flash");

        // Gemini returns a thought_signature inside each tool call and refuses the following turn without
        // it; the Microsoft.Extensions.AI OpenAI adapter drops that vendor field. One call works and the
        // turn after it does not, so an agent loop — multi-turn by definition — cannot run here. Claiming
        // the capability would let an agent pick Gemini and fail on its second step instead of being
        // steered somewhere that works. GeminiToolLimitationTests fails when this stops being true.
        Assert.False(capabilities.Supports(ModelCapability.ToolCalling));

        // Everything it really does is still claimed.
        Assert.True(capabilities.Supports(ModelCapability.Chat));
        Assert.True(capabilities.Supports(ModelCapability.Streaming));
    }

    [Fact]
    public void The_prefix_gemini_puts_on_its_own_listed_ids_is_still_recognised()
    {
        // Listing answers with "models/gemini-3.6-flash" and generation accepts that form, so a model
        // registered straight from the list must not lose the exclusion on a string-matching technicality.
        Assert.False(Capabilities("models/gemini-3.6-flash").Supports(ModelCapability.ToolCalling));
    }

    [Fact]
    public void A_model_on_a_gemini_connection_loses_tool_calling_whatever_it_is_called()
    {
        // Found by registering a real connection: Gemini serves models named things like
        // "antigravity-preview-05-2026", which reach the same endpoint through the same adapter and hit
        // the same missing thought_signature. Nothing in that name says Google. The limitation belongs to
        // the endpoint, so the preset decides it — reading the model name would have missed this entirely.
        var capabilities = ViaConnection("gemini", "models/antigravity-preview-05-2026");

        Assert.False(capabilities.Supports(ModelCapability.ToolCalling));
        Assert.True(capabilities.Supports(ModelCapability.Chat));
    }

    [Fact]
    public void The_same_model_name_on_another_service_keeps_tool_calling()
    {
        // The exclusion follows the connection, not the word. A model with an unremarkable name reached
        // through some other OpenAI-compatible service is unaffected.
        Assert.True(ViaConnection("openrouter", "models/antigravity-preview-05-2026").Supports(ModelCapability.ToolCalling));
    }

    [Fact]
    public void A_non_google_model_keeps_tool_calling()
    {
        // The exclusion is Gemini's, not a general retreat from tool calling.
        Assert.True(Capabilities("gpt-4o-mini").Supports(ModelCapability.ToolCalling));
        Assert.True(Capabilities("deepseek-chat").Supports(ModelCapability.ToolCalling));
    }

    [Fact]
    public void Gemini_embeddings_are_sized_the_way_gemini_sizes_them()
    {
        // 3072, measured against the live endpoint. The 1536 default is OpenAI's, and would misdescribe
        // every vector a knowledge base built on this model stores.
        Assert.Equal(3072, Capabilities("gemini-embedding-001").EmbeddingDimensions);
        Assert.True(Capabilities("gemini-embedding-001").Supports(ModelCapability.Embeddings));
    }

    [Fact]
    public void OpenRouter_is_not_described_as_lacking_embeddings()
    {
        var openrouter = ((IConnectionAwareProvider)Provider).Presets.Single(p => p.Id == "openrouter");

        // It said otherwise until the live API was asked: /embeddings answers and returns 1536-dimension
        // vectors. A preset that denies a capability the service has is as wrong as one that invents one,
        // and costs more, because nobody goes looking for a feature the UI says is absent.
        Assert.True(openrouter.SupportsEmbeddings);
        Assert.Contains("openai/text-embedding-3-small", openrouter.CuratedModels);
    }

    [Fact]
    public async Task The_preset_is_offered_by_the_providers_endpoint()
    {
        var providers = await _app.GetTestClient().GetStringAsync("/netcoreai/api/providers", TestContext.Current.CancellationToken);

        Assert.Contains("Google Gemini", providers, StringComparison.Ordinal);
    }
}
