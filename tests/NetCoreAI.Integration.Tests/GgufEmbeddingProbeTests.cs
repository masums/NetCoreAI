using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Backends.Gguf;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Embeddings through the GGUF provider, from the raw generator up to the full factory pipeline. These
/// narrow the ground when something goes wrong between llama.cpp and Microsoft.Extensions.AI.
/// </summary>
[Trait("Category", "Model")]
public class GgufEmbeddingProbeTests
{
    private static async Task<(GgufModelProvider Provider, ModelDescriptor Model)> SetupAsync(CancellationToken cancellationToken)
    {
        var path = await ModelFixtures.RequireEmbeddingModelAsync(cancellationToken);
        var provider = new GgufModelProvider(
            new GgufConformanceTests.StaticOptions(new NetCoreAIOptions { DataDirectory = ModelFixtures.CacheDirectory }),
            NullLoggerFactory.Instance);

        return (provider, new ModelDescriptor
        {
            Id = "nomic-embed",
            Name = "Nomic Embed Text v1.5",
            Format = ModelFormat.Gguf,
            ProviderId = GgufModelProvider.ProviderId,
            Path = path,
        });
    }

    [Fact]
    public async Task The_header_says_it_is_an_embedding_model()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);

        var capabilities = provider.GetCapabilities(model);

        Assert.True(capabilities.Supports(ModelCapability.Embeddings));
        Assert.False(capabilities.Supports(ModelCapability.Chat));
    }

    [Fact]
    public async Task The_raw_generator_embeds()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 2048 }, ct);
        try
        {
            var generator = provider.CreateEmbeddingGenerator(loaded);
            var embeddings = await generator.GenerateAsync(["the holiday policy"], cancellationToken: ct);

            Assert.Equal(768, embeddings[0].Vector.Length);
        }
        finally
        {
            await provider.UnloadAsync(loaded, ct);
        }
    }

    [Fact]
    public async Task The_same_loaded_model_embeds_repeatedly()
    {
        var (provider, model) = await SetupAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        var loaded = await provider.LoadAsync(model, new LoadOptions { ContextSize = 2048 }, ct);
        try
        {
            // Indexing a corpus asks for a generator many times; each must work, and none may take the
            // shared llama.cpp context with it when its pipeline is disposed.
            for (var i = 0; i < 3; i++)
            {
                var generator = provider.CreateEmbeddingGenerator(loaded);
                var embeddings = await generator.GenerateAsync([$"batch {i}"], cancellationToken: ct);
                Assert.Equal(768, embeddings[0].Vector.Length);
                generator.Dispose();
            }
        }
        finally
        {
            await provider.UnloadAsync(loaded, ct);
        }
    }

    [Fact]
    public async Task Embeddings_work_through_the_full_factory_pipeline()
    {
        var path = await ModelFixtures.RequireEmbeddingModelAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        var dataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = Directory.CreateDirectory(dataDirectory).FullName });
        builder.Logging.ClearProviders();
        builder.Services.AddNetCoreAI(o => o.DataDirectory = dataDirectory).AddGgufBackend();

        using var host = builder.Build();
        await host.StartAsync(ct);
        try
        {
            await host.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
            {
                Id = "nomic-embed",
                Name = "Nomic Embed Text v1.5",
                Format = ModelFormat.Gguf,
                ProviderId = GgufModelProvider.ProviderId,
                Path = path,
            }, ct);

            var generator = host.Services.GetRequiredService<IChatClientFactory>().GetEmbeddingGenerator("nomic-embed");

            // Twice: the factory rebuilds its pipeline per call, which is where a shared native handle
            // gets disposed out from under the next one.
            var first = await generator.GenerateAsync(["holiday policy"], cancellationToken: ct);
            var second = await generator.GenerateAsync(["expense policy"], cancellationToken: ct);

            Assert.Equal(768, first[0].Vector.Length);
            Assert.Equal(768, second[0].Vector.Length);
        }
        finally
        {
            await host.StopAsync(ct);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
