using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NetCoreAI.Tools;

/// <summary>Turns saved tool definitions into the functions a model can call.</summary>
public interface IToolRegistry
{
    /// <summary>
    /// The named tools, as <see cref="AIFunction"/>s bound to this caller.
    /// </summary>
    /// <param name="toolIds">Ids or names of the tools to include. Unknown ones are left out.</param>
    /// <param name="context">Who the call is on behalf of; captured by each function.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IReadOnlyList<AIFunction>> GetFunctionsAsync(
        IEnumerable<string> toolIds,
        ToolCallContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class ToolRegistry(IMetadataStore store, IToolInvoker invoker, ICodeToolSource codeTools) : IToolRegistry
{
    public async Task<IReadOnlyList<AIFunction>> GetFunctionsAsync(
        IEnumerable<string> toolIds,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        ArgumentNullException.ThrowIfNull(context);

        var wanted = toolIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return [];
        }

        var functions = new List<AIFunction>();
        foreach (var tool in await store.Tools.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (tool.Enabled && (wanted.Contains(tool.Id) || wanted.Contains(tool.Name)) && Permitted(tool, context))
            {
                functions.Add(new ToolFunction(tool, invoker, context));
            }
        }

        // Code tools come from the assembly rather than the store, but a caller naming one should not have
        // to know that: an agent lists the tools it wants and gets them, whatever they were declared by.
        if (codeTools is CodeToolSource source)
        {
            foreach (var name in wanted)
            {
                if (source.TryGet(name, out var code) && Permitted(code.Definition, context)
                    && !functions.Exists(f => f.Name == code.Definition.Name))
                {
                    functions.Add(source.Create(code));
                }
            }
        }

        return functions;
    }

    /// <summary>
    /// Whether this caller may be offered this tool at all.
    /// </summary>
    /// <remarks>
    /// An <see cref="ConfirmationPolicy.AdminOnly"/> tool is left out of the list rather than offered and
    /// refused on use: a model told about a tool will try it, and a refusal mid-turn spends a call and
    /// invites it to look for a way round. What it is never told about, it never attempts.
    /// </remarks>
    private static bool Permitted(ToolDefinition tool, ToolCallContext context) =>
        tool.Confirmation != ConfirmationPolicy.AdminOnly
        || context.User?.IsInRole("Administrator") == true
        || context.User?.IsInRole("admin") == true;
}

/// <summary>
/// One tool, as the model sees it.
/// </summary>
/// <remarks>
/// The caller is captured here rather than read at call time, so the identity a tool runs under is the one
/// that existed when the turn started. A long agent run cannot end up calling a tool as somebody else
/// because the ambient context moved underneath it.
/// </remarks>
internal sealed class ToolFunction(ToolDefinition tool, IToolInvoker invoker, ToolCallContext context) : AIFunction
{
    public override string Name => tool.Name;

    public override string Description => tool.Description ?? $"{tool.Method} {tool.Route}";

    /// <summary>Built from the model-supplied parameters alone; a locked parameter has no property here.</summary>
    public override JsonElement JsonSchema { get; } = ToolSchema.For(tool);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var supplied = arguments is null
            ? null
            : (IReadOnlyDictionary<string, object?>)arguments.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase);

        var result = await invoker.InvokeAsync(tool, supplied, context, cancellationToken).ConfigureAwait(false);
        return result.Output;
    }
}
