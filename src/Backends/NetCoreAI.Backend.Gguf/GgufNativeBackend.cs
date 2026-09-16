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

    /// <summary>
    /// Sends llama.cpp's own log lines to <paramref name="logger"/>, and keeps the error ones where a
    /// failed load can find them.
    /// </summary>
    /// <remarks>
    /// Handing <c>WithLogCallback</c> an ILogger directly is simpler and was what this did, but it sends
    /// llama.cpp's reasons somewhere the caller never sees. A load that fails throws
    /// <c>LoadWeightsFailedException</c>, whose message is only the file path: the line that actually says
    /// what is wrong — "missing tensor 'blk.32.ssm_conv1d.weight'" — went to the log alone. Somebody
    /// reading "could not load" with no cause reasonably concludes their machine is at fault.
    /// <para>
    /// llama.cpp writes in fragments rather than lines, using <see cref="LLamaLogLevel.Continue"/> to mean
    /// "still the previous line, still its level", so this reassembles them before logging or recording.
    /// </para>
    /// </remarks>
    internal static NativeLogConfig.LLamaLogCallback Forward(ILogger logger)
    {
        var line = new System.Text.StringBuilder();
        var level = LLamaLogLevel.Info;
        var gate = new Lock();

        return (messageLevel, message) =>
        {
            if (message is null)
            {
                return;
            }

            lock (gate)
            {
                if (messageLevel != LLamaLogLevel.Continue)
                {
                    level = messageLevel;
                }

                line.Append(message);

                int end;
                while ((end = IndexOfNewline(line)) >= 0)
                {
                    var text = line.ToString(0, end).TrimEnd('\r');
                    line.Remove(0, end + 1);
                    Emit(logger, level, text);
                }
            }
        };
    }

    private static int IndexOfNewline(System.Text.StringBuilder builder)
    {
        for (var i = 0; i < builder.Length; i++)
        {
            if (builder[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static void Emit(ILogger logger, LLamaLogLevel level, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        // CA2254: llama.cpp's text is the message, and it is not a template.
#pragma warning disable CA2254
        switch (level)
        {
            case LLamaLogLevel.Error:
                logger.LogError(text);
                NativeErrorCapture.Record(text);
                break;
            case LLamaLogLevel.Warning:
                logger.LogWarning(text);
                break;
            case LLamaLogLevel.Debug:
                logger.LogDebug(text);
                break;
            default:
                logger.LogInformation(text);
                break;
        }
#pragma warning restore CA2254
    }

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

                config.WithAutoFallback(true).WithLogCallback(Forward(logger));
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

/// <summary>
/// Collects llama.cpp's error lines while a model is loading, so a failure can say why.
/// </summary>
/// <remarks>
/// One capture is active at a time. A second concurrent load gets none rather than the first one's
/// lines: a load explained by another load's failure is worse than a load explained by nothing.
/// </remarks>
internal sealed class NativeErrorCapture : IDisposable
{
    private static readonly Lock Gate = new();
    private static NativeErrorCapture? _active;

    private readonly List<string> _errors = [];

    /// <summary>Starts collecting, or returns null when another load is already doing so.</summary>
    public static NativeErrorCapture? Begin()
    {
        lock (Gate)
        {
            if (_active is not null)
            {
                return null;
            }

            return _active = new NativeErrorCapture();
        }
    }

    public static void Record(string line)
    {
        lock (Gate)
        {
            _active?._errors.Add(line);
        }
    }

    /// <summary>
    /// The line worth showing somebody, or null.
    /// </summary>
    /// <remarks>
    /// The last error is the least useful one — llama.cpp finishes with "failed to load model", which
    /// only repeats what the caller already knows. The line that carries the reason is the one naming it,
    /// so that is preferred and its prefix dropped.
    /// </remarks>
    public string? Detail
    {
        get
        {
            lock (Gate)
            {
                const string Marker = "error loading model: ";
                var named = _errors.Find(e => e.Contains(Marker, StringComparison.Ordinal));
                if (named is not null)
                {
                    return named[(named.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..].Trim();
                }

                return _errors.Count > 0 ? _errors[0].Trim() : null;
            }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }
    }
}
