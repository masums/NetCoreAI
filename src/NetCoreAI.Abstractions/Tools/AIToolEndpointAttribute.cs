namespace NetCoreAI;

/// <summary>
/// Marks an endpoint as one a model may be given as a tool, and allows it to be invoked in-process.
/// </summary>
/// <remarks>
/// Discovery lists every endpoint in the host; this attribute is the developer saying "I have looked at
/// this one and it is safe to expose". Without it an endpoint can still become a tool, but only over HTTP
/// loopback, and only after an administrator enables it in the designer. See ADR-0004.
/// <para>
/// The attribute does not weaken the endpoint's own authorization. A tool call runs as the caller, so an
/// endpoint that refuses an unauthenticated user refuses the tool too.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AIToolEndpointAttribute : Attribute, IAIToolEndpointMetadata
{
    public AIToolEndpointAttribute()
    {
    }

    /// <param name="name">The name the model calls this tool by.</param>
    public AIToolEndpointAttribute(string name) => Name = name;

    /// <summary>Tool name for the model. Defaults to one derived from the route and method.</summary>
    public string? Name { get; }

    /// <summary>What the tool does and when to use it, in the words the model is given.</summary>
    public string? Description { get; set; }

    /// <summary>Whether the endpoint changes anything. Defaults to read-only, which is the safe reading.</summary>
    public ToolSafety Safety { get; set; } = ToolSafety.ReadOnly;
}

/// <summary>Endpoint metadata saying a model may be given this endpoint as a tool.</summary>
/// <remarks>
/// An interface as well as an attribute so minimal APIs can opt in fluently with <c>.WithAITool()</c>,
/// which has no method to attribute.
/// </remarks>
public interface IAIToolEndpointMetadata
{
    string? Name { get; }

    string? Description { get; }

    ToolSafety Safety { get; }
}
