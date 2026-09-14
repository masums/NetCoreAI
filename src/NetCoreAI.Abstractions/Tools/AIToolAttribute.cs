namespace NetCoreAI;

/// <summary>
/// Marks a method as a tool a model may call.
/// </summary>
/// <remarks>
/// For work the host already does in code, where an endpoint would be ceremony: a method takes typed
/// parameters, returns a result, and its signature is the schema. The method's own parameter names and
/// types are what the model is shown, so they are worth naming for a reader rather than for the compiler.
/// <para>
/// A code tool runs in the host's process as the host, not through an endpoint, so nothing evaluates
/// authorization for it. Anything a caller should not be able to do indirectly must check that itself.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AIToolAttribute : Attribute
{
    public AIToolAttribute()
    {
    }

    /// <param name="name">The name the model calls this tool by.</param>
    public AIToolAttribute(string name) => Name = name;

    /// <summary>Tool name for the model. Defaults to the method name in snake_case.</summary>
    public string? Name { get; }

    /// <summary>What it does and when to use it, in the words the model is given.</summary>
    public string? Description { get; set; }

    /// <summary>Whether calling it changes anything. Read-only by default, which is the safe reading.</summary>
    public ToolSafety Safety { get; set; } = ToolSafety.ReadOnly;
}
