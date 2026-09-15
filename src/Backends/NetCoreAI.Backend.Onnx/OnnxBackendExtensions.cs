using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

/// <summary>Registering a cross-encoder reranker.</summary>
public static class OnnxRerankerExtensions
{
    /// <summary>
    /// Re-reads retrieved passages with a local cross-encoder and reorders them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Point it at a folder holding an ONNX export and its tokenizer — <c>bge-reranker-base</c> is the
    /// usual choice. Nothing else has to change: every knowledge base reranks by default once one is
    /// registered, and none does while none is.
    /// </para>
    /// <para>
    /// A folder that is missing or unreadable is a startup failure rather than a silent downgrade. A host
    /// that asked for reranking and quietly did not get it would never find out.
    /// </para>
    /// </remarks>
    public static NetCoreAIBuilder AddOnnxReranker(this NetCoreAIBuilder builder, string modelDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        builder.Services.AddSingleton<NetCoreAI.Knowledge.IReranker>(sp =>
            new NetCoreAI.Backends.Onnx.OnnxReranker(
                modelDirectory,
                sp.GetRequiredService<ILogger<NetCoreAI.Backends.Onnx.OnnxReranker>>()));

        return builder;
    }
}
