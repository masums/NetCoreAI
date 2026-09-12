using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetCoreAI.Backends.Gguf;
using NetCoreAI.Conformance;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Runs the provider contract against llama.cpp with real weights.
/// Set NETCOREAI_TEST_MODELS=1 to enable; otherwise every test returns early.
/// </summary>
[Trait("Category", "Model")]
public class GgufConformanceTests : ModelProviderConformanceTests
{
    private string? _modelPath;

    protected override Task<IModelProvider> CreateProviderAsync()
    {
        var options = new NetCoreAIOptions { DataDirectory = ModelFixtures.CacheDirectory };
        return Task.FromResult<IModelProvider>(new GgufModelProvider(new StaticOptions(options), NullLoggerFactory.Instance));
    }

    protected override async Task<ModelDescriptor?> CreateChatModelAsync()
    {
        _modelPath = await ModelFixtures.GetChatModelAsync(TestContext.Current.CancellationToken);
        return _modelPath is null ? null : new ModelDescriptor
        {
            Id = "qwen2.5-0.5b-instruct",
            Name = "Qwen2.5 0.5B Instruct",
            Format = ModelFormat.Gguf,
            ProviderId = GgufModelProvider.ProviderId,
            Path = _modelPath,
        };
    }

    protected override LoadOptions LoadOptions => new() { ContextSize = 1024, GpuLayers = 0 };

    internal sealed class StaticOptions(NetCoreAIOptions value) : IOptionsMonitor<NetCoreAIOptions>
    {
        public NetCoreAIOptions CurrentValue => value;

        public NetCoreAIOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NetCoreAIOptions, string?> listener) => null;
    }
}

/// <summary>
/// GGUF behaviour beyond the shared contract: the chat template, grammar-constrained output and
/// the whole registry-to-IChatClient path with a real model.
/// </summary>
[Trait("Category", "Model")]
public class GgufModelTests
{
    private static async Task<(GgufModelProvider Provider, ModelDescriptor Model)> SetupAsync(CancellationToken cancellationToken)
    {
        var path = await ModelFixtures.RequireChatModelAsync(cancellationToken);
        var provider = new GgufModelProvider(
            new GgufConformanceTests.StaticOptions(new NetCoreAIOptions { DataDirectory = ModelFixtures.CacheDirectory }),
            NullLoggerFactory.Instance);

        return (provider, new ModelDescriptor
        {
            Id = "qwen-test",
            Name = "Qwen2.5 0.5B Instruct",
            Format = ModelFormat.Gguf,
            ProviderId = GgufModelProvider.ProviderId,
            Path = path,
        });
    }

    [Fact]
    public async Task Header_metadata_matches_the_real_file()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);

        var metadata = GgufMetadataReader.Read(model.Path!);

        Assert.Equal("qwen2", metadata.Architecture);
        Assert.Equal(24, metadata.BlockCount);
        Assert.Equal(896, metadata.EmbeddingLength);
        Assert.Equal(14, metadata.HeadCount);
        Assert.Equal(2, metadata.HeadCountKv);
        Assert.Equal("Q4_K_M", metadata.Quantization);
        Assert.False(metadata.IsEmbeddingModel);
        Assert.False(string.IsNullOrWhiteSpace(metadata.ChatTemplate));
        Assert.True(provider.GetCapabilities(model).Supports(ModelCapability.Chat));
    }

    [Fact]
    public async Task Chat_template_from_the_file_wraps_messages_in_the_expected_markers()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 512 }, TestContext.Current.CancellationToken);
        try
        {
            var client = provider.CreateChatClient(loaded);
            // The client type is internal but visible to tests, so the prompt can be checked directly.
            var text = ((GgufChatClient)client).BuildPrompt(
                [new ChatMessage(ChatRole.System, "You are terse."), new ChatMessage(ChatRole.User, "Hello")],
                null);
            Assert.Contains("You are terse.", text, StringComparison.Ordinal);
            Assert.Contains("Hello", text, StringComparison.Ordinal);
            // Qwen uses ChatML markers, which proves the template came from the file rather than the fallback.
            Assert.Contains("<|im_start|>", text, StringComparison.Ordinal);
        }
        finally
        {
            await provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Generation_answers_a_factual_prompt()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        try
        {
            var client = provider.CreateChatClient(loaded);
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "What is the capital of France? Answer with the city name only.")],
                new ChatOptions { MaxOutputTokens = 16, Temperature = 0f },
                TestContext.Current.CancellationToken);

            Assert.Contains("Paris", response.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(response.Usage);
            Assert.True(response.Usage!.InputTokenCount > 0, "prompt tokens must be counted");
            Assert.True(response.Usage.OutputTokenCount > 0, "generated tokens must be counted");
        }
        finally
        {
            await provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Grammar_forces_output_that_matches_the_requested_json_schema()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);

        var schema = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""
            { "type": "object",
              "properties": { "city": { "type": "string" }, "population": { "type": "integer" } },
              "required": ["city", "population"] }
            """);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        try
        {
            var client = provider.CreateChatClient(loaded);
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Give me the capital of France and its population as JSON.")],
                new ChatOptions
                {
                    MaxOutputTokens = 64,
                    Temperature = 0f,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(schema),
                },
                TestContext.Current.CancellationToken);

            // The grammar guarantees this parses and carries both required properties.
            using var parsed = System.Text.Json.JsonDocument.Parse(response.Text);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, parsed.RootElement.ValueKind);
            Assert.True(parsed.RootElement.TryGetProperty("city", out var city));
            Assert.True(parsed.RootElement.TryGetProperty("population", out var population));
            Assert.Equal(System.Text.Json.JsonValueKind.String, city.ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Number, population.ValueKind);
        }
        finally
        {
            await provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Repeated_generations_reuse_one_executor_instead_of_leaking_a_context()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 512 }, TestContext.Current.CancellationToken);
        try
        {
            var client = provider.CreateChatClient(loaded);
            for (var i = 0; i < 4; i++)
            {
                await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, $"Say {i}")],
                    new ChatOptions { MaxOutputTokens = 4, Temperature = 0f },
                    TestContext.Current.CancellationToken);
            }

            // Each StatelessExecutor allocates a llama.cpp context with a large compute buffer, so
            // sequential requests must hand the same one back and forth rather than create four.
            Assert.Equal(1, GgufModelProvider.HandleOf(loaded).ExecutorCount);
        }
        finally
        {
            await provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Concurrent_generations_each_get_their_own_executor()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 512 }, TestContext.Current.CancellationToken);
        try
        {
            var client = provider.CreateChatClient(loaded);
            var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, $"Say {i}")],
                new ChatOptions { MaxOutputTokens = 4, Temperature = 0f },
                TestContext.Current.CancellationToken)));

            Assert.All(responses, r => Assert.False(string.IsNullOrWhiteSpace(r.Text)));
            // Executors are not shared mid-request, so three parallel calls need three of them.
            Assert.InRange(GgufModelProvider.HandleOf(loaded).ExecutorCount, 1, 3);
        }
        finally
        {
            await provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Model_reaches_application_code_through_the_registry_and_IChatClient()
    {
        var path = await ModelFixtures.RequireChatModelAsync(TestContext.Current.CancellationToken);

        var dataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings { ContentRootPath = Directory.CreateDirectory(dataDirectory).FullName });
        builder.Logging.ClearProviders();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = dataDirectory;
            o.Models.DefaultContextSize = 1024;
        }).AddGgufBackend();

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var registry = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IModelRegistry>(host.Services);
            await registry.RegisterAsync(new ModelDescriptor
            {
                Id = "qwen-registry",
                Name = "Qwen2.5 0.5B Instruct",
                Format = ModelFormat.Gguf,
                ProviderId = GgufModelProvider.ProviderId,
                Path = path,
            }, TestContext.Current.CancellationToken);

            // Exactly what host application code would write: inject IChatClient, ask a question.
            var chat = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IChatClient>(host.Services);
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Say the word ready and nothing else.")],
                new ChatOptions { MaxOutputTokens = 12, Temperature = 0f },
                TestContext.Current.CancellationToken);

            Assert.False(string.IsNullOrWhiteSpace(response.Text));
            Assert.Equal(ModelStatus.Loaded, (await registry.GetAsync("qwen-registry", TestContext.Current.CancellationToken))!.Status);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
