using NetCoreAI.Hardware;
using Xunit;

namespace NetCoreAI.Core.Tests;

public class FitEstimatorTests
{
    private static ModelDescriptor Gguf(long sizeBytes, string quant = "Q4_K_M", long? parameters = null) => new()
    {
        Id = "m",
        Name = "m",
        Format = ModelFormat.Gguf,
        ProviderId = "gguf",
        SizeBytes = sizeBytes,
        Quantization = quant,
        ParameterCount = parameters,
        ContextLength = 8192,
    };

    [Fact]
    public void Generic_estimate_adds_headroom_and_kv_cache()
    {
        var weights = 4L * 1024 * 1024 * 1024; // 4 GB Q4 ≈ 7B
        var estimate = FitEstimator.Generic(Gguf(weights, parameters: 7_000_000_000), new LoadOptions { ContextSize = 4096 });

        Assert.True(estimate.TotalBytes > weights * 1.1, "must include KV cache on top of 10 % headroom");
        Assert.True(estimate.TotalBytes < weights * 2, "KV cache for 4k context must stay well under the weight size");
        Assert.Equal(0, estimate.VramBytes);
    }

    [Fact]
    public void Full_gpu_offload_moves_everything_to_vram()
    {
        var estimate = FitEstimator.Generic(Gguf(1_000_000_000), new LoadOptions { ContextSize = 2048, GpuLayers = -1 });
        Assert.Equal(0, estimate.RamBytes);
        Assert.True(estimate.VramBytes > 1_000_000_000);
    }

    [Fact]
    public void Partial_offload_splits_proportionally()
    {
        var estimate = FitEstimator.Generic(Gguf(1_000_000_000), new LoadOptions { ContextSize = 2048, GpuLayers = 16 });
        Assert.True(estimate.RamBytes > 0 && estimate.VramBytes > 0);
        Assert.InRange((double)estimate.VramBytes / estimate.TotalBytes, 0.45, 0.55);
    }

    [Fact]
    public void Unknown_size_yields_unknown_verdict()
    {
        var estimate = FitEstimator.Generic(Gguf(0) with { SizeBytes = null }, LoadOptions.Default);
        Assert.Equal(FitVerdict.Unknown, estimate.Verdict);
    }
}
