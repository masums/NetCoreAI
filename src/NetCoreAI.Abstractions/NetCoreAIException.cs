namespace NetCoreAI;

/// <summary>Base type for all NetCoreAI errors so hosts can catch them in one place.</summary>
public class NetCoreAIException : Exception
{
    public NetCoreAIException() { }
    public NetCoreAIException(string message) : base(message) { }
    public NetCoreAIException(string message, Exception? innerException) : base(message, innerException) { }
}

public sealed class ModelNotFoundException(string idOrAlias)
    : NetCoreAIException($"No model or alias named '{idOrAlias}' is registered. Add one in the dashboard (Model Hub or Providers) or via IModelRegistry.")
{
    public string IdOrAlias { get; } = idOrAlias;
}

public sealed class ProviderNotFoundException(string providerIdOrFormat)
    : NetCoreAIException($"No provider can serve '{providerIdOrFormat}'. Register a backend package (e.g. AddGgufBackend(), AddOllamaBackend()).")
{
    public string ProviderIdOrFormat { get; } = providerIdOrFormat;
}

public sealed class ModelWontFitException(ModelDescriptor model, MemoryEstimate estimate, long availableBytes)
    : NetCoreAIException($"Model '{model.Name}' needs about {estimate.TotalBytes / 1_048_576} MB but only {availableBytes / 1_048_576} MB is available within the memory budget. {estimate.Explanation}")
{
    public ModelDescriptor Model { get; } = model;
    public MemoryEstimate Estimate { get; } = estimate;
    public long AvailableBytes { get; } = availableBytes;
}

public sealed class RemoteProvidersDisabledException(string modelIdOrAlias)
    : NetCoreAIException($"'{modelIdOrAlias}' is served by a remote provider, but remote providers are disabled in NetCoreAI settings (Providers > Remote enabled).")
{
    public string ModelIdOrAlias { get; } = modelIdOrAlias;
}

public sealed class ModelBusyException(string modelId)
    : NetCoreAIException($"Model '{modelId}' is busy and its concurrency policy rejects queued requests.")
{
    public string ModelId { get; } = modelId;
}

public sealed class ConnectionNotFoundException(string connectionId)
    : NetCoreAIException($"Provider connection '{connectionId}' does not exist or is disabled.")
{
    public string ConnectionId { get; } = connectionId;
}
