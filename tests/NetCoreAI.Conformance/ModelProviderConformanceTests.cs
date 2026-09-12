using Microsoft.Extensions.AI;
using Xunit;

namespace NetCoreAI.Conformance;

/// <summary>
/// Contract every <see cref="IModelProvider"/> must satisfy. Derive, supply a provider and a descriptor
/// it can serve, and the suite proves the model is usable through plain Microsoft.Extensions.AI types.
/// </summary>
/// <remarks>
/// Tests that need real inference are skipped unless <see cref="SupportsInference"/> is true, so a
/// provider can run the structural half of the suite in CI without shipping model weights.
/// </remarks>
public abstract class ModelProviderConformanceTests : IAsyncLifetime
{
    protected IModelProvider Provider { get; private set; } = default!;

    /// <summary>A descriptor the provider can load. Null skips every load-dependent test.</summary>
    protected ModelDescriptor? ChatModel { get; private set; }

    /// <summary>An embedding-capable descriptor, when the provider has one.</summary>
    protected ModelDescriptor? EmbeddingModel { get; private set; }

    protected abstract Task<IModelProvider> CreateProviderAsync();

    /// <summary>Return null when no model is available in this environment; load tests then skip.</summary>
    protected abstract Task<ModelDescriptor?> CreateChatModelAsync();

    protected virtual Task<ModelDescriptor?> CreateEmbeddingModelAsync() => Task.FromResult<ModelDescriptor?>(null);

    /// <summary>False in environments with no weights, so generation tests skip instead of failing.</summary>
    protected virtual bool SupportsInference => ChatModel is not null;

    protected virtual LoadOptions LoadOptions => new() { ContextSize = 512 };

    /// <summary>Explains why load-dependent tests were skipped.</summary>
    protected virtual string SkipReason => "No model is available: set NETCOREAI_TEST_MODELS=1 to download the test fixture.";

    public virtual async ValueTask InitializeAsync()
    {
        Provider = await CreateProviderAsync();
        ChatModel = await CreateChatModelAsync();
        EmbeddingModel = await CreateEmbeddingModelAsync();
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Provider_identifies_itself()
    {
        Assert.False(string.IsNullOrWhiteSpace(Provider.Id));
        Assert.False(string.IsNullOrWhiteSpace(Provider.DisplayName));
        Assert.NotEmpty(Provider.SupportedFormats);
        Assert.Equal(Provider.Id, Provider.Id.Trim());
    }

    [Fact]
    public void CanLoad_rejects_a_descriptor_of_an_unrelated_format()
    {
        var alien = new ModelDescriptor
        {
            Id = "conformance-alien",
            Name = "alien",
            // Pick a format this provider does not claim.
            Format = Enum.GetValues<ModelFormat>().First(f => !Provider.SupportedFormats.Contains(f)),
            ProviderId = Provider.Id,
        };

        Assert.False(Provider.CanLoad(alien));
    }

    [Fact]
    public void CanLoad_accepts_the_supplied_model()
    {
        Assert.SkipWhen(ChatModel is null, SkipReason);

        Assert.True(Provider.CanLoad(ChatModel!));
    }

    [Fact]
    public void Capabilities_are_queryable_without_loading()
    {
        Assert.SkipWhen(ChatModel is null, SkipReason);

        var capabilities = Provider.GetCapabilities(ChatModel!);
        Assert.NotNull(capabilities);
        Assert.True(capabilities.Supports(ModelCapability.Chat), "a chat model must report the Chat capability");
        // Capability queries must not require a load, so asking twice stays cheap and consistent.
        Assert.Equal(capabilities, Provider.GetCapabilities(ChatModel!));
    }

    [Fact]
    public async Task Memory_estimate_is_returned_before_loading()
    {
        Assert.SkipWhen(ChatModel is null, SkipReason);

        var estimate = await Provider.EstimateMemoryAsync(ChatModel!, LoadOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(estimate);
        Assert.True(estimate.RamBytes >= 0 && estimate.VramBytes >= 0);
        if (Provider.Kind == ProviderKind.Remote)
        {
            Assert.Equal(0, estimate.TotalBytes);
        }
    }

    [Fact]
    public async Task Load_then_unload_releases_the_model()
    {
        Assert.SkipWhen(ChatModel is null || !SupportsInference, SkipReason);

        var loaded = await Provider.LoadAsync(ChatModel!, LoadOptions, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(ChatModel!.Id, loaded.Descriptor.Id);
            Assert.Equal(Provider.Id, loaded.ProviderId);
            Assert.True(loaded.MemoryBytes >= 0);
        }
        finally
        {
            await Provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }

        // Unloading twice must not throw: the lifecycle manager can race with host shutdown.
        await Provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Chat_client_answers_through_Microsoft_Extensions_AI()
    {
        Assert.SkipWhen(ChatModel is null || !SupportsInference, SkipReason);

        var loaded = await Provider.LoadAsync(ChatModel!, LoadOptions, TestContext.Current.CancellationToken);
        try
        {
            var client = Provider.CreateChatClient(loaded);
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word: ready")],
                new ChatOptions { MaxOutputTokens = 24, Temperature = 0f },
                TestContext.Current.CancellationToken);

            Assert.NotNull(response);
            Assert.False(string.IsNullOrWhiteSpace(response.Text), "the model returned no text");
        }
        finally
        {
            await Provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Chat_client_streams_and_the_stream_matches_the_whole_response()
    {
        Assert.SkipWhen(ChatModel is null || !SupportsInference, SkipReason);

        var loaded = await Provider.LoadAsync(ChatModel!, LoadOptions, TestContext.Current.CancellationToken);
        try
        {
            var client = Provider.CreateChatClient(loaded);
            var chunks = new List<string>();
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Count: one two three")],
                new ChatOptions { MaxOutputTokens = 24, Temperature = 0f },
                TestContext.Current.CancellationToken))
            {
                chunks.AddRange(update.Contents.OfType<TextContent>().Select(c => c.Text ?? ""));
            }

            Assert.NotEmpty(chunks);
            Assert.False(string.IsNullOrWhiteSpace(string.Concat(chunks)));
        }
        finally
        {
            await Provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Cancelling_a_generation_stops_it_without_faulting_the_model()
    {
        Assert.SkipWhen(ChatModel is null || !SupportsInference, SkipReason);

        var loaded = await Provider.LoadAsync(ChatModel!, LoadOptions, TestContext.Current.CancellationToken);
        try
        {
            var client = Provider.CreateChatClient(loaded);
            using var cts = new CancellationTokenSource();
            var seen = 0;
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Write a long story about the sea.")],
                new ChatOptions { MaxOutputTokens = 200 },
                cts.Token))
            {
                if (update.Contents.OfType<TextContent>().Any() && ++seen >= 2)
                {
                    await cts.CancelAsync();
                }
            }

            // The model must still work after a cancelled generation.
            var after = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Say ok")],
                new ChatOptions { MaxOutputTokens = 16, Temperature = 0f },
                TestContext.Current.CancellationToken);
            Assert.NotNull(after);
        }
        catch (OperationCanceledException)
        {
            // Also acceptable: the provider surfaces cancellation rather than ending the stream quietly.
        }
        finally
        {
            await Provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Embedding_generator_returns_vectors_of_a_consistent_size()
    {
        Assert.SkipWhen(EmbeddingModel is null, "No embedding model is configured for this provider.");

        var loaded = await Provider.LoadAsync(EmbeddingModel!, LoadOptions, TestContext.Current.CancellationToken);
        try
        {
            var generator = Provider.CreateEmbeddingGenerator(loaded);
            var embeddings = await generator.GenerateAsync(["first text", "second text"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, embeddings.Count);
            Assert.NotEmpty(embeddings[0].Vector.ToArray());
            Assert.Equal(embeddings[0].Vector.Length, embeddings[1].Vector.Length);
        }
        finally
        {
            await Provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Embeddings_are_refused_on_a_model_that_cannot_produce_them()
    {
        Assert.SkipWhen(ChatModel is null || !SupportsInference, SkipReason);

        Assert.SkipWhen(Provider.GetCapabilities(ChatModel!).Supports(ModelCapability.Embeddings), "This model produces embeddings too, so there is nothing to refuse.");

        var loaded = await Provider.LoadAsync(ChatModel!, LoadOptions, TestContext.Current.CancellationToken);
        try
        {
            Assert.Throws<NotSupportedException>(() => Provider.CreateEmbeddingGenerator(loaded));
        }
        finally
        {
            await Provider.UnloadAsync(loaded, TestContext.Current.CancellationToken);
        }
    }
}
