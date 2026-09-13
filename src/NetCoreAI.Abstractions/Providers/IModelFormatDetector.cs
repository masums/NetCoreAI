namespace NetCoreAI;

/// <summary>
/// What a backend could work out about a model file or folder by inspecting it, without loading weights.
/// </summary>
/// <param name="Format">Weight format the backend recognised.</param>
/// <param name="ProviderId">Id of the provider that can serve it.</param>
public sealed record DetectedModel(ModelFormat Format, string ProviderId)
{
    /// <summary>Architecture family: llama, qwen2, phi3, bert...</summary>
    public string? Family { get; init; }

    /// <summary>Quantization label, e.g. Q4_K_M or int4.</summary>
    public string? Quantization { get; init; }

    public int? ContextLength { get; init; }

    public long? ParameterCount { get; init; }

    /// <summary>Bytes on disk: the file, or every weight file in the folder.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Chat template found in the file or folder, when it carries one.</summary>
    public string? ChatTemplate { get; init; }

    public ModelCapabilities Capabilities { get; init; } = ModelCapabilities.None;

    /// <summary>Suggested display name, when the metadata carries one.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// Implemented by local backends so import and download can identify a model without Core knowing any
/// weight format. Detectors are discovered through DI and asked in registration order; the first that
/// recognises the path wins.
/// </summary>
public interface IModelFormatDetector
{
    /// <summary>Inspects a file or folder; returns null when this backend does not recognise it.</summary>
    DetectedModel? TryDetect(string path);
}
