using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace NetCoreAI.Backends.Onnx;

/// <summary>
/// Maps the NetCoreAI execution-provider preference onto ONNX Runtime. Unlike llama.cpp, ONNX Runtime
/// chooses its provider per session, so the preference applies at every load rather than once per process.
/// </summary>
internal static class OnnxExecutionProviders
{
    /// <summary>The ONNX Runtime GenAI provider name, or null for plain CPU execution.</summary>
    public static string? GenAiProviderName(ExecutionProvider preference) => preference switch
    {
        ExecutionProvider.Cuda => "cuda",
        ExecutionProvider.DirectML => "dml",
        ExecutionProvider.Vulkan => "webgpu",
        ExecutionProvider.Npu => "qnn",
        _ => null,
    };

    /// <summary>
    /// Applies the preference to a GenAI config. The provider list in genai_config.json is replaced, so the
    /// setting wins over whatever the model folder shipped with; a provider whose native library is missing
    /// throws, and the caller falls back to CPU.
    /// </summary>
    public static bool TryApply(Config config, ExecutionProvider preference, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (GenAiProviderName(preference) is not { } provider)
        {
            config.ClearProviders();
            return true;
        }

        try
        {
            config.ClearProviders();
            config.AppendProvider(provider);
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeGenAIException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(
                ex,
                "ONNX Runtime has no {Provider} execution provider in this process; falling back to the CPU. Add the matching Microsoft.ML.OnnxRuntimeGenAI GPU package to enable it.",
                provider);
            config.ClearProviders();
            return false;
        }
    }

    /// <summary>Session options for an embedding model, which runs on plain ONNX Runtime rather than GenAI.</summary>
    public static SessionOptions CreateSessionOptions(ExecutionProvider preference, int? threads, ILogger logger)
    {
        var options = new SessionOptions();
        try
        {
            if (threads is > 0)
            {
                options.IntraOpNumThreads = threads.Value;
            }

            switch (preference)
            {
                case ExecutionProvider.Cuda:
                    options.AppendExecutionProvider_CUDA();
                    break;
                case ExecutionProvider.DirectML:
                    options.AppendExecutionProvider_DML();
                    break;
                default:
                    // CPU: the default provider is always present.
                    break;
            }
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(ex, "Could not enable the {Provider} execution provider for the embedding session; using the CPU.", preference);
        }

        return options;
    }
}
