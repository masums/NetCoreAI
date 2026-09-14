namespace NetCoreAI;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<GpuVendor>))]
public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Apple,
    Qualcomm,
}

/// <summary>One GPU as seen by the probe.</summary>
public sealed record GpuInfo(int Index, string Name, GpuVendor Vendor, long? TotalVramBytes, long? FreeVramBytes, IReadOnlyList<ExecutionProvider> ExecutionProviders)
{
    public string? DriverVersion { get; init; }
}

/// <summary>Snapshot of the machine the host runs on.</summary>
public sealed record HardwareInfo
{
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required int LogicalCores { get; init; }
    public required long TotalRamBytes { get; init; }
    public required long AvailableRamBytes { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public bool HasNpu { get; init; }
    public DateTimeOffset ProbedAt { get; init; } = DateTimeOffset.UtcNow;

    public long TotalVramBytes => Gpus.Sum(g => g.TotalVramBytes ?? 0);

    public long FreeVramBytes => Gpus.Sum(g => g.FreeVramBytes ?? 0);

    /// <summary>All execution providers any GPU (or the CPU) supports.</summary>
    public IReadOnlyList<ExecutionProvider> AvailableExecutionProviders
        => Gpus.SelectMany(g => g.ExecutionProviders).Distinct().Prepend(ExecutionProvider.Cpu).ToList();
}

/// <summary>Detects CPU/RAM/GPU/NPU and caches the result for a short time.</summary>
public interface IHardwareProbe
{
    ValueTask<HardwareInfo> ProbeAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

/// <summary>Estimates whether a model fits and recommends load options for this hardware.</summary>
public interface IFitEstimator
{
    ValueTask<MemoryEstimate> EstimateAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default);

    /// <summary>Suggested options (GPU layers, context) that fit the current hardware, or null when nothing fits.</summary>
    ValueTask<LoadOptions?> RecommendAsync(ModelDescriptor model, CancellationToken cancellationToken = default);
}
