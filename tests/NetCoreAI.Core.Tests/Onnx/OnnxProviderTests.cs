using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetCoreAI.Backends.Onnx;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Providers;
using Xunit;

namespace NetCoreAI.Core.Tests.Onnx;

/// <summary>
/// Everything the ONNX provider can answer without loading a graph: capabilities, memory estimates,
/// registry wiring and the error paths. Generation is covered by the model-gated conformance suite.
/// </summary>
public class OnnxProviderTests : IDisposable
{
    private readonly OnnxFolderBuilder _builder = new();

    private static OnnxModelProvider CreateProvider(Action<NetCoreAIOptions>? configure = null)
    {
        var options = new NetCoreAIOptions { DataDirectory = Path.GetTempPath() };
        configure?.Invoke(options);
        return new OnnxModelProvider(new StaticOptionsMonitor(options), NullLoggerFactory.Instance);
    }

    private static ModelDescriptor Descriptor(string path, ModelFormat format = ModelFormat.Onnx) => new()
    {
        Id = "local-onnx",
        Name = "Local ONNX model",
        Format = format,
        ProviderId = OnnxModelProvider.ProviderId,
        Path = path,
    };

    [Fact]
    public void Provider_identity_matches_the_onnx_format()
    {
        var provider = CreateProvider();

        Assert.Equal("onnx", provider.Id);
        Assert.Equal(ProviderKind.Local, provider.Kind);
        Assert.Equal([ModelFormat.Onnx], provider.SupportedFormats);
    }

    [Fact]
    public void CanLoad_requires_the_onnx_format_and_a_path()
    {
        var provider = CreateProvider();
        var path = _builder.GenerativeFolder();

        Assert.True(provider.CanLoad(Descriptor(path)));
        Assert.False(provider.CanLoad(Descriptor(path, ModelFormat.Gguf)));
        Assert.False(provider.CanLoad(Descriptor(path) with { Path = null }));
    }

    [Fact]
    public void Capabilities_come_from_the_folder_configuration()
    {
        var provider = CreateProvider();

        var chat = provider.GetCapabilities(Descriptor(_builder.GenerativeFolder()));
        var embed = provider.GetCapabilities(Descriptor(_builder.EmbeddingFolder()));

        Assert.True(chat.Supports(ModelCapability.Chat));
        Assert.Equal(32768, chat.MaxContext);
        Assert.True(embed.Supports(ModelCapability.Embeddings));
        Assert.False(embed.Supports(ModelCapability.Chat));
        Assert.Equal(384, embed.EmbeddingDimensions);
    }

    [Fact]
    public void Capabilities_already_on_the_descriptor_are_trusted()
    {
        var provider = CreateProvider();
        var declared = new ModelCapabilities(ModelCapability.Chat | ModelCapability.ToolCalling, 1024);

        Assert.Equal(declared, provider.GetCapabilities(Descriptor(_builder.GenerativeFolder()) with { Capabilities = declared }));
    }

    [Fact]
    public void Capabilities_fall_back_when_the_folder_is_missing()
    {
        var provider = CreateProvider();

        var capabilities = provider.GetCapabilities(Descriptor("nowhere/missing-folder") with { ContextLength = 2048 });

        Assert.True(capabilities.Supports(ModelCapability.Chat));
        Assert.Equal(2048, capabilities.MaxContext);
    }

    [Fact]
    public async Task Memory_estimate_uses_the_real_layer_and_head_counts()
    {
        var provider = CreateProvider();
        var descriptor = Descriptor(_builder.GenerativeFolder());

        var small = await provider.EstimateMemoryAsync(descriptor, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        var large = await provider.EstimateMemoryAsync(descriptor, new LoadOptions { ContextSize = 8192 }, TestContext.Current.CancellationToken);

        Assert.True(large.TotalBytes > small.TotalBytes, "a larger context must need more memory");
        Assert.Contains("KV cache", large.Explanation, StringComparison.OrdinalIgnoreCase);

        // The CPU execution provider keeps everything in RAM.
        Assert.Equal(0, small.VramBytes);
    }

    [Fact]
    public async Task A_gpu_execution_provider_moves_the_whole_estimate_to_vram()
    {
        var provider = CreateProvider();
        var descriptor = Descriptor(_builder.GenerativeFolder());

        var cpu = await provider.EstimateMemoryAsync(descriptor, new LoadOptions { ContextSize = 1024 }, TestContext.Current.CancellationToken);
        var gpu = await provider.EstimateMemoryAsync(
            descriptor,
            new LoadOptions { ContextSize = 1024, ExecutionProvider = ExecutionProvider.Cuda },
            TestContext.Current.CancellationToken);

        // ONNX Runtime does not split layers across devices the way llama.cpp does: it is all or nothing.
        Assert.Equal(0, cpu.VramBytes);
        Assert.Equal(0, gpu.RamBytes);
        Assert.Equal(cpu.TotalBytes, gpu.TotalBytes);
    }

    [Fact]
    public async Task Embedding_estimates_cover_the_graph_without_a_kv_cache()
    {
        var provider = CreateProvider();

        var estimate = await provider.EstimateMemoryAsync(Descriptor(_builder.EmbeddingFolder()), LoadOptions.Default, TestContext.Current.CancellationToken);

        Assert.True(estimate.TotalBytes > 90_000);
        Assert.Contains("encoder graph", estimate.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kv_cache_estimate_accounts_for_grouped_query_attention()
    {
        var grouped = OnnxModelFolderReader.Read(_builder.GenerativeFolder());          // 14 heads, 2 kv heads
        var full = grouped with { HeadCountKv = grouped.HeadCount };

        var groupedBytes = OnnxModelProvider.EstimateKvCacheBytes(grouped, 4096);
        var fullBytes = OnnxModelProvider.EstimateKvCacheBytes(full, 4096);

        Assert.True(groupedBytes * 5 < fullBytes, "two key/value heads must cost far less than fourteen");
    }

    [Fact]
    public async Task Loading_a_missing_folder_fails_with_a_clear_message()
    {
        var provider = CreateProvider();
        var descriptor = Descriptor(Path.Combine(Path.GetTempPath(), "netcoreai-tests", "definitely-missing-onnx"));

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await provider.LoadAsync(descriptor, LoadOptions.Default, TestContext.Current.CancellationToken));

        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(descriptor.Name, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loading_a_folder_that_holds_no_model_explains_what_is_needed()
    {
        var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await provider.LoadAsync(Descriptor(_builder.EmptyFolder()), LoadOptions.Default, TestContext.Current.CancellationToken));

        Assert.Contains("genai_config.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loading_a_descriptor_without_a_path_fails_with_a_clear_message()
    {
        var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await provider.LoadAsync(Descriptor("x") with { Path = null }, LoadOptions.Default, TestContext.Current.CancellationToken));

        Assert.Contains("no folder path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Relative_paths_resolve_against_the_data_directory()
    {
        var path = _builder.GenerativeFolder();
        var provider = CreateProvider(o => o.DataDirectory = Path.GetDirectoryName(path)!);

        var capabilities = provider.GetCapabilities(Descriptor(Path.GetFileName(path)));

        Assert.True(capabilities.Supports(ModelCapability.Chat));
        Assert.Equal(32768, capabilities.MaxContext);
    }

    [Fact]
    public async Task Registering_the_backend_adds_exactly_one_provider()
    {
        using var host = await TestHost.StartAsync(b => b.AddOnnxBackend().AddOnnxBackend());
        var providers = host.Services.GetRequiredService<IProviderRegistry>();

        Assert.Single(providers.All, p => p.Id == OnnxModelProvider.ProviderId);
        Assert.NotNull(providers.Get("onnx"));
    }

    [Fact]
    public async Task Registry_routes_an_onnx_model_to_the_onnx_provider()
    {
        using var host = await TestHost.StartAsync(b => b.AddGgufBackend().AddOnnxBackend());
        var registry = host.Services.GetRequiredService<IModelRegistry>();

        var registered = await registry.RegisterAsync(new ModelDescriptor
        {
            Id = "qwen-onnx",
            Name = "Qwen2.5 0.5B Instruct (ONNX)",
            Format = ModelFormat.Onnx,
            ProviderId = OnnxModelProvider.ProviderId,
            Path = _builder.GenerativeFolder(),
        }, TestContext.Current.CancellationToken);

        // Capabilities were filled in from the folder config at registration time, and the GGUF provider
        // registered alongside must not claim the model.
        Assert.True(registered.Capabilities.Supports(ModelCapability.Chat));
        Assert.Equal(OnnxModelProvider.ProviderId, host.Services.GetRequiredService<IProviderRegistry>().Resolve(registered).Id);
    }

    public void Dispose()
    {
        _builder.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class StaticOptionsMonitor(NetCoreAIOptions value) : IOptionsMonitor<NetCoreAIOptions>
    {
        public NetCoreAIOptions CurrentValue => value;

        public NetCoreAIOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NetCoreAIOptions, string?> listener) => null;
    }
}
