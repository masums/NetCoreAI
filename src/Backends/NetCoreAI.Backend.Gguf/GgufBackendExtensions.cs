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
        return builder.AddProvider<GgufModelProvider>();
    }
}
