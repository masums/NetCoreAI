using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetCoreAI.Backends.Gguf;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Providers;
using Xunit;

namespace NetCoreAI.Core.Tests.Gguf;

/// <summary>
/// Everything the GGUF provider can answer without loading weights: capabilities, memory estimates and
/// the error paths. Generation itself is covered by the model-gated conformance suite.
/// </summary>
public class GgufProviderTests : IDisposable
{
    private readonly List<string> _directories = [];

    private static GgufModelProvider CreateProvider(Action<NetCoreAIOptions>? configure = null)
    {
        var options = new NetCoreAIOptions { DataDirectory = Path.GetTempPath() };
        configure?.Invoke(options);
        return new GgufModelProvider(new StaticOptionsMonitor(options), NullLoggerFactory.Instance);
    }

    private string WriteModel(GgufHeaderWriter writer, string name = "model")
    {
        var path = writer.ToFile(name);
        _directories.Add(Path.GetDirectoryName(path)!);
        return path;
    }

    private static ModelDescriptor Descriptor(string path, ModelFormat format = ModelFormat.Gguf) => new()
    {
        Id = "local-model",
        Name = "Local model",
        Format = format,
        ProviderId = GgufModelProvider.ProviderId,
        Path = path,
    };

    [Fact]
    public void Provider_identity_matches_the_gguf_format()
    {
        var provider = CreateProvider();

        Assert.Equal("gguf", provider.Id);
        Assert.Equal(ProviderKind.Local, provider.Kind);
        Assert.Equal([ModelFormat.Gguf], provider.SupportedFormats);
    }

    [Fact]
    public void CanLoad_requires_the_gguf_format_and_a_path()
    {
        var provider = CreateProvider();
        var path = WriteModel(GgufHeaderWriter.ChatModel());

        Assert.True(provider.CanLoad(Descriptor(path)));
        Assert.False(provider.CanLoad(Descriptor(path, ModelFormat.Onnx)));
        Assert.False(provider.CanLoad(Descriptor(path) with { Path = null }));
    }

    [Fact]
    public void Capabilities_come_from_the_file_header()
    {
        var provider = CreateProvider();
        var chat = provider.GetCapabilities(Descriptor(WriteModel(GgufHeaderWriter.ChatModel())));
        var embed = provider.GetCapabilities(Descriptor(WriteModel(GgufHeaderWriter.EmbeddingModel(), "embed")));

        Assert.True(chat.Supports(ModelCapability.Chat));
        Assert.True(chat.Supports(ModelCapability.StructuredOutput));
        Assert.Equal(32768, chat.MaxContext);

        Assert.True(embed.Supports(ModelCapability.Embeddings));
        Assert.False(embed.Supports(ModelCapability.Chat));
    }

    [Fact]
    public void Capabilities_already_on_the_descriptor_are_trusted()
    {
        var provider = CreateProvider();
        var declared = new ModelCapabilities(ModelCapability.Chat | ModelCapability.ToolCalling, 1024);
        var descriptor = Descriptor(WriteModel(GgufHeaderWriter.ChatModel())) with { Capabilities = declared };

        Assert.Equal(declared, provider.GetCapabilities(descriptor));
    }

    [Fact]
    public void Capabilities_fall_back_when_the_file_is_missing()
    {
        var provider = CreateProvider();
        var capabilities = provider.GetCapabilities(Descriptor("nowhere/missing.gguf") with { ContextLength = 2048 });

        Assert.True(capabilities.Supports(ModelCapability.Chat));
        Assert.Equal(2048, capabilities.MaxContext);
    }

    [Fact]
    public async Task Memory_estimate_uses_the_real_layer_and_head_counts()
    {
        var provider = CreateProvider();
        var descriptor = Descriptor(WriteModel(GgufHeaderWriter.ChatModel()));

        var small = await provider.EstimateMemoryAsync(descriptor, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        var large = await provider.EstimateMemoryAsync(descriptor, new LoadOptions { ContextSize = 8192 }, TestContext.Current.CancellationToken);

        Assert.True(large.TotalBytes > small.TotalBytes, "a larger context must need more memory");
        Assert.Contains("KV cache", large.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, small.VramBytes);   // CPU-only by default
    }

    [Fact]
    public async Task Full_gpu_offload_moves_the_estimate_to_vram()
    {
        var provider = CreateProvider();
        var descriptor = Descriptor(WriteModel(GgufHeaderWriter.ChatModel()));

        var cpu = await provider.EstimateMemoryAsync(descriptor, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        var gpu = await provider.EstimateMemoryAsync(descriptor, new LoadOptions { ContextSize = 1024, GpuLayers = -1 }, TestContext.Current.CancellationToken);
        var half = await provider.EstimateMemoryAsync(descriptor, new LoadOptions { ContextSize = 1024, GpuLayers = 12 }, TestContext.Current.CancellationToken);

        Assert.Equal(0, cpu.VramBytes);
        Assert.Equal(0, gpu.RamBytes);
        Assert.True(gpu.VramBytes > 0);
        // The header says 24 layers, so 12 offloaded is half the model.
        Assert.InRange((double)half.VramBytes / half.TotalBytes, 0.45, 0.55);
    }

    [Theory]
    [InlineData(KvCacheType.Default)]
    [InlineData(KvCacheType.F16)]
    [InlineData(KvCacheType.Q8)]
    [InlineData(KvCacheType.Q4)]
    public void Quantised_kv_cache_shrinks_the_estimate(KvCacheType type)
    {
        var metadata = GgufMetadataReader.Read(GgufHeaderWriter.ChatModel().ToStream());
        var f16 = GgufModelProvider.EstimateKvCacheBytes(metadata, 4096, KvCacheType.F16);
        var actual = GgufModelProvider.EstimateKvCacheBytes(metadata, 4096, type);

        Assert.True(actual > 0);
        Assert.True(type is KvCacheType.Q8 or KvCacheType.Q4 ? actual < f16 : actual == f16);
    }

    [Fact]
    public void Kv_cache_estimate_accounts_for_grouped_query_attention()
    {
        var grouped = GgufMetadataReader.Read(GgufHeaderWriter.ChatModel().ToStream());          // 14 heads, 2 kv heads
        var full = grouped with { HeadCountKv = grouped.HeadCount };

        var groupedBytes = GgufModelProvider.EstimateKvCacheBytes(grouped, 4096, KvCacheType.F16);
        var fullBytes = GgufModelProvider.EstimateKvCacheBytes(full, 4096, KvCacheType.F16);

        Assert.True(groupedBytes * 5 < fullBytes, "two key/value heads must cost far less than fourteen");
    }

    [Fact]
    public async Task Loading_a_missing_file_fails_with_a_clear_message()
    {
        var provider = CreateProvider();
        var descriptor = Descriptor(Path.Combine(Path.GetTempPath(), "netcoreai-tests", "definitely-missing.gguf"));

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await provider.LoadAsync(descriptor, LoadOptions.Default, TestContext.Current.CancellationToken));

        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(descriptor.Name, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loading_a_descriptor_without_a_path_fails_with_a_clear_message()
    {
        var provider = CreateProvider();
        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await provider.LoadAsync(Descriptor("x") with { Path = null }, LoadOptions.Default, TestContext.Current.CancellationToken));

        Assert.Contains("no file path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Relative_paths_resolve_against_the_data_directory()
    {
        var path = WriteModel(GgufHeaderWriter.ChatModel());
        var dataDirectory = Path.GetDirectoryName(path)!;
        var provider = CreateProvider(o => o.DataDirectory = dataDirectory);

        var capabilities = provider.GetCapabilities(Descriptor(Path.GetFileName(path)));

        Assert.True(capabilities.Supports(ModelCapability.Chat));
        Assert.Equal(32768, capabilities.MaxContext);
    }

    [Fact]
    public async Task Registering_the_backend_adds_exactly_one_provider()
    {
        using var host = await TestHost.StartAsync(b => b.AddGgufBackend().AddGgufBackend());
        var providers = host.Services.GetRequiredService<IProviderRegistry>();

        Assert.Single(providers.All, p => p.Id == GgufModelProvider.ProviderId);
        Assert.NotNull(providers.Get("gguf"));
    }

    [Fact]
    public async Task Registry_routes_a_gguf_model_to_the_gguf_provider()
    {
        using var host = await TestHost.StartAsync(b => b.AddGgufBackend());
        var registry = host.Services.GetRequiredService<IModelRegistry>();
        var path = WriteModel(GgufHeaderWriter.ChatModel());

        var registered = await registry.RegisterAsync(new ModelDescriptor
        {
            Id = "qwen-local",
            Name = "Qwen local",
            Format = ModelFormat.Gguf,
            ProviderId = GgufModelProvider.ProviderId,
            Path = path,
        }, TestContext.Current.CancellationToken);

        // Capabilities were filled in from the file header at registration time.
        Assert.True(registered.Capabilities.Supports(ModelCapability.Chat));
        Assert.Equal(GgufModelProvider.ProviderId, host.Services.GetRequiredService<IProviderRegistry>().Resolve(registered).Id);

        var aliases = await registry.GetAliasesAsync(TestContext.Current.CancellationToken);
        Assert.Equal("qwen-local", aliases[ModelAlias.Default].ModelId);
    }

    public void Dispose()
    {
        foreach (var directory in _directories.Distinct())
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    private sealed class StaticOptionsMonitor(NetCoreAIOptions value) : IOptionsMonitor<NetCoreAIOptions>
    {
        public NetCoreAIOptions CurrentValue => value;

        public NetCoreAIOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NetCoreAIOptions, string?> listener) => null;
    }
}
