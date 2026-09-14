using LLama.Native;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Backends.Gguf;

/// <summary>
/// Configures the llama.cpp native library once, before the first model load.
/// llama.cpp picks its backend at load time and cannot be reconfigured afterwards, so the first
/// load in the process wins and later changes to the execution-provider setting need a restart.
/// </summary>
internal static class GgufNativeBackend
{
    private static readonly Lock Gate = new();
    private static bool _configured;

    /// <summary>The execution provider actually applied, once configuration has happened.</summary>
    public static ExecutionProvider? Applied { get; private set; }

    public static void EnsureConfigured(ExecutionProvider preference, ILogger logger)
    {
        lock (Gate)
        {
            if (_configured)
            {
                if (Applied is { } applied && applied != preference && preference != ExecutionProvider.Auto)
                {
                    logger.LogWarning(
                        "llama.cpp is already initialised with execution provider {Applied}; the request for {Requested} takes effect after a host restart.",
                        applied, preference);
                }

                return;
            }

            try
            {
                var config = NativeLibraryConfig.All;
                switch (preference)
                {
                    case ExecutionProvider.Cpu:
                        config.WithCuda(false).WithVulkan(false);
                        break;
                    case ExecutionProvider.Cuda:
                        config.WithCuda(true).WithVulkan(false);
                        break;
                    case ExecutionProvider.Vulkan:
                        config.WithVulkan(true).WithCuda(false);
                        break;
                    case ExecutionProvider.Metal:
                        // Metal lives in the macOS native build and is selected automatically there.
                        break;
                    case ExecutionProvider.DirectML:
                    case ExecutionProvider.Npu:
                        logger.LogInformation("The GGUF backend has no {Provider} build; llama.cpp will use Vulkan or the CPU instead.", preference);
                        break;
                    case ExecutionProvider.Auto:
                    default:
                        break;
                }

                config.WithAutoFallback(true).WithLogCallback(logger);
                _configured = true;
                Applied = preference;
            }
            catch (InvalidOperationException ex)
            {
                // Thrown when something already loaded the native library; nothing to configure any more.
                logger.LogDebug(ex, "llama.cpp native library was already loaded; keeping its existing configuration.");
                _configured = true;
                Applied = ExecutionProvider.Auto;
            }
        }
    }
}
