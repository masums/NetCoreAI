using Microsoft.AspNetCore.Builder;

namespace NetCoreAI;

/// <summary>Opting an endpoint in as a model tool.</summary>
public static class AIToolEndpointExtensions
{
    /// <summary>
    /// Marks this endpoint as one a model may be given as a tool, and allows it to be invoked in-process.
    /// </summary>
    /// <param name="builder">The endpoint being built.</param>
    /// <param name="name">Tool name for the model; defaults to one derived from the route.</param>
    /// <param name="description">What the tool does and when to use it, written for the model.</param>
    /// <param name="safety">Whether calling it changes anything. Read-only by default.</param>
    /// <remarks>
    /// The fluent equivalent of <see cref="AIToolEndpointAttribute"/>, for minimal APIs whose handlers are
    /// lambdas with nothing to attribute. It does not weaken the endpoint's authorization: a tool call runs
    /// as the caller, so an endpoint that refuses them refuses the tool. See ADR-0004.
    /// </remarks>
    public static TBuilder WithAITool<TBuilder>(
        this TBuilder builder,
        string? name = null,
        string? description = null,
        ToolSafety safety = ToolSafety.ReadOnly)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new AIToolMetadata(name, description, safety));
        return builder;
    }

    private sealed record AIToolMetadata(string? Name, string? Description, ToolSafety Safety) : IAIToolEndpointMetadata;
}
