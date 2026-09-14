using Microsoft.Extensions.Options;
using NetCoreAI.Providers;

namespace NetCoreAI.Hardware;

/// <summary>
/// Generic estimate: weights (file size, or params × bits) × 1.1 headroom + KV cache. Providers can supply
/// a better number through <see cref="IModelProvider.EstimateMemoryAsync"/>; this class is the fallback.
/// </summary>
internal sealed class FitEstimator(IHardwareProbe hardware, IProviderRegistry providers, IOptionsMonitor<NetCoreAIOptions> options) : IFitEstimator
{
    private const double Headroom = 1.10;

    public async ValueTask<MemoryEstimate> EstimateAsync(ModelDescriptor model, LoadOptions loadOptions, CancellationToken cancellationToken = default)
    {
        if (model.IsRemote)
        {
            return new MemoryEstimate(0, 0, FitVerdict.Fits, "Remote model; no local memory needed.");
        }

        MemoryEstimate estimate = MemoryEstimate.Unknown;
        try
        {
            var provider = providers.Resolve(model);
            estimate = await provider.EstimateMemoryAsync(model, loadOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (NetCoreAIException)
        {
            // no provider yet (e.g. estimating before download): use the generic formula
        }

        if (estimate.Verdict == FitVerdict.Unknown)
        {
            estimate = Generic(model, loadOptions);
        }

        var hw = await hardware.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var budget = options.CurrentValue.Models.MemoryBudgetBytes ?? (long)(hw.TotalRamBytes * 0.8) + hw.TotalVramBytes;
        var ramAvailable = Math.Min(hw.AvailableRamBytes, budget);
        var vramAvailable = hw.FreeVramBytes > 0 ? hw.FreeVramBytes : hw.TotalVramBytes;

        var verdict = Verdict(estimate.RamBytes, ramAvailable) is var r && Verdict(estimate.VramBytes, vramAvailable) is var v
            ? (FitVerdict)Math.Max((int)r, (int)v)
            : FitVerdict.Unknown;

        return estimate with { Verdict = verdict, Explanation = estimate.Explanation ?? $"Needs ~{estimate.TotalBytes / 1_048_576} MB; {ramAvailable / 1_048_576} MB RAM and {vramAvailable / 1_048_576} MB VRAM available." };
    }

    public async ValueTask<LoadOptions?> RecommendAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
    {
        var hw = await hardware.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var ctx = Math.Min(model.ContextLength ?? options.CurrentValue.Models.DefaultContextSize, options.CurrentValue.Models.DefaultContextSize);

        // Try full GPU, then CPU; halve context until it fits.
        foreach (var gpuLayers in hw.Gpus.Count > 0 ? new[] { -1, 0 } : [0])
        {
            for (var c = ctx; c >= 512; c /= 2)
            {
                var candidate = new LoadOptions { ContextSize = c, GpuLayers = gpuLayers, ExecutionProvider = options.CurrentValue.Models.ExecutionProvider };
                var est = await EstimateAsync(model, candidate, cancellationToken).ConfigureAwait(false);
                if (est.Verdict is FitVerdict.Fits or FitVerdict.Tight)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static MemoryEstimate Generic(ModelDescriptor model, LoadOptions o)
    {
        var weights = model.SizeBytes ?? (model.ParameterCount is { } p ? (long)(p * BitsPerParam(model.Quantization) / 8.0) : 0);
        if (weights == 0)
        {
            return MemoryEstimate.Unknown;
        }

        var ctx = o.ContextSize ?? model.ContextLength ?? 4096;
        // KV cache ≈ 2 × layers × hidden × ctx × bytes/elem. Without architecture details use a per-token heuristic
        // scaled by model size (≈ 0.5 MB/token for 7B at f16, proportional otherwise).
        var paramsB = (model.ParameterCount ?? EstimateParams(weights, model.Quantization)) / 1e9;
        var bytesPerElem = o.KvCacheType is KvCacheType.Q8 ? 1.0 : o.KvCacheType is KvCacheType.Q4 ? 0.5 : 2.0;
        var kv = (long)((double)ctx * 512 * 1024 * (paramsB / 7.0) * (bytesPerElem / 2.0) / 4.0);
        var total = (long)((weights + kv) * Headroom);

        var gpuLayers = o.GpuLayers ?? 0;
        var onGpu = gpuLayers == -1 ? total : gpuLayers == 0 ? 0 : (long)(total * Math.Min(1.0, gpuLayers / 32.0));
        return new MemoryEstimate(total - onGpu, onGpu, FitVerdict.Unknown, null);
    }

    private static double BitsPerParam(string? quant)
    {
        if (string.IsNullOrEmpty(quant))
        {
            return 16;
        }

        var q = quant.ToUpperInvariant();
        return q.Contains("F32") ? 32 : q.Contains("F16") || q.Contains("BF16") ? 16 : q.Contains("Q8") || q.Contains("INT8") ? 8.5
            : q.Contains("Q6") ? 6.6 : q.Contains("Q5") ? 5.7 : q.Contains("Q4") || q.Contains("INT4") ? 4.8 : q.Contains("Q3") ? 3.9 : q.Contains("Q2") ? 3.2 : 16;
    }

    private static long EstimateParams(long weightBytes, string? quant) => (long)(weightBytes * 8 / BitsPerParam(quant));

    private static FitVerdict Verdict(long needed, long available)
    {
        if (needed <= 0)
        {
            return FitVerdict.Fits;
        }

        if (available <= 0)
        {
            return FitVerdict.WontFit;
        }

        return needed > available ? FitVerdict.WontFit : needed > available * 0.9 ? FitVerdict.Tight : FitVerdict.Fits;
    }
}
