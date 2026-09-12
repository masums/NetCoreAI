using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NetCoreAI;

/// <summary>
/// Fluent surface returned by <c>AddNetCoreAI()</c>. Backend, vector store and storage packages add extension methods on it.
/// </summary>
public sealed class NetCoreAIBuilder
{
    internal NetCoreAIBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    /// <summary>Registers a model provider. Idempotent per implementation type.</summary>
    public NetCoreAIBuilder AddProvider<TProvider>() where TProvider : class, IModelProvider
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProvider, TProvider>());
        return this;
    }

    /// <summary>Registers a model hub source (Hugging Face is added by default).</summary>
    public NetCoreAIBuilder AddModelSource<TSource>() where TSource : class, IModelSource
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelSource, TSource>());
        return this;
    }

    /// <summary>Replaces the metadata store. Storage packages call this; the last registration wins.</summary>
    public NetCoreAIBuilder UseMetadataStore<TStore>() where TStore : class, IMetadataStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IMetadataStore, TStore>());
        return this;
    }
}
