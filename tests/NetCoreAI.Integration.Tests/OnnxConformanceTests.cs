using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Backends.Onnx;
using NetCoreAI.Conformance;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Runs the provider contract against ONNX Runtime GenAI with real weights.
/// Set NETCOREAI_TEST_MODELS=1 to enable; otherwise every test skips.
/// </summary>
[Trait("Category", "Model")]
public class OnnxConformanceTests : ModelProviderConformanceTests
{
    protected override Task<IModelProvider> CreateProviderAsync()
    {
        var options = new NetCoreAIOptions { DataDirectory = ModelFixtures.CacheDirectory };
        return Task.FromResult<IModelProvider>(
            new OnnxModelProvider(new GgufConformanceTests.StaticOptions(options), NullLoggerFactory.Instance));
    }

    protected override async Task<ModelDescriptor?> CreateChatModelAsync()
    {
        var path = await ModelFixtures.GetOnnxChatModelAsync(TestContext.Current.CancellationToken);
        return path is null ? null : new ModelDescriptor
        {
            Id = "qwen2.5-0.5b-instruct-onnx",
            Name = "Qwen2.5 0.5B Instruct (ONNX int4)",
            Format = ModelFormat.Onnx,
            ProviderId = OnnxModelProvider.ProviderId,
            Path = path,
        };
    }

    protected override async Task<ModelDescriptor?> CreateEmbeddingModelAsync()
    {
        var path = await ModelFixtures.GetOnnxEmbeddingModelAsync(TestContext.Current.CancellationToken);
        return path is null ? null : new ModelDescriptor
        {
            Id = "all-minilm-l6-v2-onnx",
            Name = "all-MiniLM-L6-v2 (ONNX)",
            Format = ModelFormat.Onnx,
            ProviderId = OnnxModelProvider.ProviderId,
            Path = path,
        };
    }

    protected override LoadOptions LoadOptions => new() { ContextSize = 1024, ExecutionProvider = ExecutionProvider.Cpu };
}

/// <summary>
/// ONNX behaviour beyond the shared contract: the chat template, generation and pooled embeddings,
/// plus the whole registry-to-IChatClient path with a real model.
/// </summary>
[Trait("Category", "Model")]
public class OnnxModelTests
{
    private static OnnxModelProvider CreateProvider() => new(
        new GgufConformanceTests.StaticOptions(new NetCoreAIOptions { DataDirectory = ModelFixtures.CacheDirectory }),
        NullLoggerFactory.Instance);

    private static async Task<(OnnxModelProvider Provider, ModelDescriptor Model)> ChatSetupAsync(CancellationToken cancellationToken)
    {
        var path = await ModelFixtures.RequireOnnxChatModelAsync(cancellationToken);
        return (CreateProvider(), new ModelDescriptor
        {
            Id = "qwen-onnx-test",
            Name = "Qwen2.5 0.5B Instruct (ONNX int4)",
            Format = ModelFormat.Onnx,
            ProviderId = OnnxModelProvider.ProviderId,
            Path = path,
        });
    }

    [Fact]
    public async Task Folder_configuration_matches_the_real_export()
    {
        var (provider, model) = await ChatSetupAsync(TestContext.Current.CancellationToken);

        var folder = OnnxModelFolderReader.Read(model.Path!);

        Assert.Equal(OnnxModelKind.Generative, folder.Kind);
        Assert.Equal("qwen2", folder.ModelType);
        Assert.Equal(24, folder.LayerCount);
        Assert.Equal(2, folder.HeadCountKv);
        Assert.Equal("int4", folder.Quantization);

        // This export keeps its template in chat_template.jinja rather than tokenizer_config.json.
        Assert.True(folder.HasChatTemplate);
        Assert.True(provider.GetCapabilities(model).Supports(ModelCapability.Chat));
    }

    [Fact]
    public async Task Chat_template_from_the_folder_wraps_messages_in_the_expected_markers()
    {
        var (provider, model) = await ChatSetupAsync(TestContext.Current.CancellationToken);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 512 }, TestContext.Current.CancellationToken);
        try
        {
            // The client type is internal but visible to tests, so the prompt can be checked directly.
            var text = ((OnnxChatClient)provider.CreateChatClient(loaded)).BuildPrompt(
                [new ChatMessage(ChatRole.System, "You are terse."), new ChatMessage(ChatRole.User, "Hello")],
                null);

            Assert.Contains("You are terse.", text, StringComparison.Ordinal);
            Assert.Contains("Hello", text, StringComparison.Ordinal);
            // Qwen uses ChatML markers, which proves the template was applied rather than the fallback.
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
        var (provider, model) = await ChatSetupAsync(TestContext.Current.CancellationToken);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        try
        {
            var response = await provider.CreateChatClient(loaded).GetResponseAsync(
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
    public async Task Streaming_delivers_tokens_before_the_response_is_complete()
    {
        var (provider, model) = await ChatSetupAsync(TestContext.Current.CancellationToken);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        try
        {
            var updates = new List<string>();
            await foreach (var update in provider.CreateChatClient(loaded).GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Count from one to five.")],
                new ChatOptions { MaxOutputTokens = 32, Temperature = 0f },
                TestContext.Current.CancellationToken))
            {
                if (update.Text is { Length: > 0 } text)
                {
                    updates.Add(text);
                }
            }

            Assert.True(updates.Count > 1, "streaming must deliver more than one chunk");
        }
        finally
        {
            await provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Stop_sequences_cut_the_output()
    {
        var (provider, model) = await ChatSetupAsync(TestContext.Current.CancellationToken);

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        try
        {
            var response = await provider.CreateChatClient(loaded).GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Write the numbers one to ten, one per line, as digits.")],
                new ChatOptions { MaxOutputTokens = 64, Temperature = 0f, StopSequences = ["5"] },
                TestContext.Current.CancellationToken);

            // ONNX Runtime GenAI has no stop-sequence option, so the provider enforces it during streaming.
            Assert.DoesNotContain("5", response.Text, StringComparison.Ordinal);
        }
        finally
        {
            await provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Embeddings_are_pooled_normalized_and_semantically_ordered()
    {
        var path = await ModelFixtures.RequireOnnxEmbeddingModelAsync(TestContext.Current.CancellationToken);
        var provider = CreateProvider();
        var model = new ModelDescriptor
        {
            Id = "minilm-onnx-test",
            Name = "all-MiniLM-L6-v2 (ONNX)",
            Format = ModelFormat.Onnx,
            ProviderId = OnnxModelProvider.ProviderId,
            Path = path,
        };

        Assert.True(provider.GetCapabilities(model).Supports(ModelCapability.Embeddings));

        var loaded = await provider.LoadAsync(model, LoadOptions.Default, TestContext.Current.CancellationToken);
        try
        {
            var generator = provider.CreateEmbeddingGenerator(loaded);
            var vectors = await generator.GenerateAsync(
                ["a cat sleeping on a sofa", "a kitten napping on a couch", "quarterly revenue grew by twelve percent"],
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(3, vectors.Count);
            Assert.All(vectors, v => Assert.Equal(384, v.Vector.Length));

            // Normalized vectors have unit length, which is what the SQLite vector store assumes.
            Assert.All(vectors, v => Assert.Equal(1.0, Magnitude(v.Vector.Span), 2));

            var related = TensorPrimitivesCosine(vectors[0].Vector.Span, vectors[1].Vector.Span);
            var unrelated = TensorPrimitivesCosine(vectors[0].Vector.Span, vectors[2].Vector.Span);
            Assert.True(related > unrelated, $"the two cat sentences must be closer ({related:F3}) than the finance one ({unrelated:F3})");
        }
        finally
        {
            await provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Model_reaches_application_code_through_the_registry_and_IChatClient()
    {
        var path = await ModelFixtures.RequireOnnxChatModelAsync(TestContext.Current.CancellationToken);

        var dataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings { ContentRootPath = Directory.CreateDirectory(dataDirectory).FullName });
        builder.Logging.ClearProviders();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = dataDirectory;
            o.Models.DefaultContextSize = 1024;
        }).AddOnnxBackend();

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var registry = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IModelRegistry>(host.Services);
            await registry.RegisterAsync(new ModelDescriptor
            {
                Id = "qwen-onnx-registry",
                Name = "Qwen2.5 0.5B Instruct (ONNX int4)",
                Format = ModelFormat.Onnx,
                ProviderId = OnnxModelProvider.ProviderId,
                Path = path,
            }, TestContext.Current.CancellationToken);

            // Exactly what host application code would write: inject IChatClient, ask a question.
            var chat = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IChatClient>(host.Services);
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Say the word ready and nothing else.")],
                new ChatOptions { MaxOutputTokens = 12, Temperature = 0f },
                TestContext.Current.CancellationToken);

            Assert.False(string.IsNullOrWhiteSpace(response.Text));
            Assert.Equal(ModelStatus.Loaded, (await registry.GetAsync("qwen-onnx-registry", TestContext.Current.CancellationToken))!.Status);
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

    private static double Magnitude(ReadOnlySpan<float> vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        return Math.Sqrt(sum);
    }

    private static double TensorPrimitivesCosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return dot / (Magnitude(a) * Magnitude(b));
    }
}
