using NetCoreAI.Backends.OpenAICompatible;

namespace NetCoreAI;
public static class OpenAICompatibleBackendExtensions
{
    /// <summary>Adds the OpenAI-compatible provider. Connections (OpenAI, Azure, Gemini, vLLM, LM Studio, Groq...) are configured in the dashboard.</summary>
    public static NetCoreAIBuilder AddOpenAICompatibleBackend(this NetCoreAIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProvider<OpenAICompatibleProvider>();
    }
}
