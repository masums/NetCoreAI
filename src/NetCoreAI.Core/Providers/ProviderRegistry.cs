using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Providers;

/// <summary>Finds the provider for a model: explicit provider id first, then format + hardware + user preference.</summary>
public interface IProviderRegistry
{
    IReadOnlyList<IModelProvider> All { get; }

    IModelProvider? Get(string providerId);

    /// <summary>Returns the provider that will serve the descriptor, honouring disabled providers and the remote master switch.</summary>
    IModelProvider Resolve(ModelDescriptor model);

    bool IsEnabled(IModelProvider provider);
}

internal sealed class ProviderRegistry(IEnumerable<IModelProvider> providers, IOptionsMonitor<NetCoreAIOptions> options, ILogger<ProviderRegistry> logger) : IProviderRegistry
{
    private readonly IReadOnlyList<IModelProvider> _providers = providers.ToList();

    public IReadOnlyList<IModelProvider> All => _providers;

    public IModelProvider? Get(string providerId) => _providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled(IModelProvider provider)
    {
        var o = options.CurrentValue.Providers;
        if (o.Disabled.Contains(provider.Id, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return provider.Kind != ProviderKind.Remote || o.RemoteEnabled;
    }

    public IModelProvider Resolve(ModelDescriptor model)
    {
        if (!string.IsNullOrEmpty(model.ProviderId))
        {
            var explicitProvider = Get(model.ProviderId);
            if (explicitProvider is not null)
            {
                if (!IsEnabled(explicitProvider))
                {
                    throw explicitProvider.Kind == ProviderKind.Remote && !options.CurrentValue.Providers.RemoteEnabled
                        ? new RemoteProvidersDisabledException(model.Id)
                        : new ProviderNotFoundException($"{model.ProviderId} (disabled)");
                }

                return explicitProvider;
            }

            logger.LogWarning("Model {ModelId} references unknown provider {ProviderId}; falling back to format-based selection.", model.Id, model.ProviderId);
        }

        // Format-based selection. Registration order = user preference (first registered wins); CanLoad = hardware check.
        var candidate = _providers
            .Where(p => p.SupportedFormats.Contains(model.Format) && IsEnabled(p) && p.CanLoad(model))
            .FirstOrDefault();

        return candidate ?? throw new ProviderNotFoundException(model.Format.ToString());
    }
}
