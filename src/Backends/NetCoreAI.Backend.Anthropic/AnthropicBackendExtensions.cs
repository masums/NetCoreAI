using NetCoreAI.Backends.Anthropic;

namespace NetCoreAI;
public static class AnthropicBackendExtensions
{
    /// <summary>Adds the Anthropic (Claude) provider. Connections are configured in the dashboard.</summary>
    public static NetCoreAIBuilder AddAnthropicBackend(this NetCoreAIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProvider<AnthropicProvider>();
    }
}
