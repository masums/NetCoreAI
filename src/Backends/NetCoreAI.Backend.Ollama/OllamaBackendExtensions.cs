using NetCoreAI.Backends.Ollama;
using Microsoft.Extensions.DependencyInjection;

namespace NetCoreAI;
public static class OllamaBackendExtensions
{
    /// <summary>Adds the Ollama provider. Point a connection at http://localhost:11434 or any Ollama server in the dashboard.</summary>
    public static NetCoreAIBuilder AddOllamaBackend(this NetCoreAIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpClient("NetCoreAI.Ollama");
        return builder.AddProvider<OllamaProvider>();
    }
}
