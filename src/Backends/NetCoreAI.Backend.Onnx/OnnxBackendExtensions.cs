using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetCoreAI.Backends.Onnx;

namespace NetCoreAI;

public static class OnnxBackendExtensions
{
    /// <summary>
    /// Adds the ONNX provider, which runs ONNX Runtime GenAI folders (genai_config.json) and
    /// sentence-transformers ONNX embedding exports in-process. The CPU execution provider ships with this
    /// package; add Microsoft.ML.OnnxRuntimeGenAI.Cuda or .DirectML to the host project for GPU execution,
    /// then set the execution provider in NetCoreAI settings.
    /// </summary>
    public static NetCoreAIBuilder AddOnnxBackend(this NetCoreAIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Lets import and the download manager identify ONNX folders without Core knowing the format.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelFormatDetector, OnnxFormatDetector>());
        return builder.AddProvider<OnnxModelProvider>();
    }
}
