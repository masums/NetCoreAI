using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetCoreAI.Backends.Gguf;

namespace NetCoreAI;

public static class GgufBackendExtensions
{
    /// <summary>
    /// Adds the GGUF provider, which runs models in-process with llama.cpp. The CPU native backend ships with
    /// this package; add LLamaSharp.Backend.Cuda12, .Vulkan or .Metal to the host project for GPU acceleration,
    /// then set the execution provider in NetCoreAI settings.
    /// </summary>
    public static NetCoreAIBuilder AddGgufBackend(this NetCoreAIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Lets import and the download manager identify .gguf files without Core knowing the format.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelFormatDetector, GgufFormatDetector>());
        return builder.AddProvider<GgufModelProvider>();
    }
}
