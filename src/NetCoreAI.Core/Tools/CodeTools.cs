using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace NetCoreAI.Tools;

/// <summary>One <c>[AITool]</c> method, and how to build a callable function from it.</summary>
internal sealed record CodeTool(ToolDefinition Definition, Type DeclaringType, MethodInfo Method);

/// <summary>The <c>[AITool]</c> methods this host registered.</summary>
public interface ICodeToolSource
{
    /// <summary>Their definitions, for listing them in the designer.</summary>
    IReadOnlyList<ToolDefinition> Definitions { get; }
}

/// <summary>
/// Code tools, discovered once at registration rather than scanned at every call.
/// </summary>
/// <remarks>
/// A code tool is not stored: it exists because the assembly declares it, so it comes back with the
/// process and cannot drift from what the code says. That is also why it is read-only in the designer —
/// editing a row would be editing a copy of something the compiler owns.
/// </remarks>
internal sealed class CodeToolSource(IEnumerable<CodeTool> tools, IServiceScopeFactory scopes) : ICodeToolSource
{
    private readonly List<CodeTool> _tools = [.. tools];

    public IReadOnlyList<ToolDefinition> Definitions => [.. _tools.Select(t => t.Definition)];

    public bool TryGet(string idOrName, out CodeTool tool)
    {
        tool = _tools.FirstOrDefault(t =>
            string.Equals(t.Definition.Id, idOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Definition.Name, idOrName, StringComparison.Ordinal))!;

        return tool is not null;
    }

    /// <summary>
    /// Builds the function, resolving the declaring type from DI for an instance method.
    /// </summary>
    /// <remarks>
    /// The scope lives as long as the function, not as long as one call: a tool holding a DbContext must
    /// not have it disposed between the model deciding to call it and the call happening.
    /// </remarks>
    public AIFunction Create(CodeTool tool)
    {
        if (tool.Method.IsStatic)
        {
            return AIFunctionFactory.Create(tool.Method, target: null, new AIFunctionFactoryOptions
            {
                Name = tool.Definition.Name,
                Description = tool.Definition.Description,
            });
        }

        var scope = scopes.CreateScope();
        var target = scope.ServiceProvider.GetRequiredService(tool.DeclaringType);
        return AIFunctionFactory.Create(tool.Method, target, new AIFunctionFactoryOptions
        {
            Name = tool.Definition.Name,
            Description = tool.Definition.Description,
        });
    }
}

/// <summary>Registering the host's own methods as tools.</summary>
public static class CodeToolExtensions
{
    /// <summary>
    /// Registers every <c>[AITool]</c> method on <typeparamref name="T"/>, and <typeparamref name="T"/>
    /// itself if its tools are instance methods.
    /// </summary>
    /// <typeparam name="T">The type holding the methods.</typeparam>
    /// <param name="builder">The NetCoreAI builder.</param>
    /// <exception cref="NetCoreAIException">When the type has no <c>[AITool]</c> method.</exception>
    public static NetCoreAIBuilder AddAITool<T>(this NetCoreAIBuilder builder)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        var methods = typeof(T)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<AIToolAttribute>() is not null)
            .ToList();

        if (methods.Count == 0)
        {
            // Silently registering nothing would leave someone wondering why their tool never appears.
            throw new NetCoreAIException($"{typeof(T).Name} has no method marked [AITool], so there is nothing to register.");
        }

        if (methods.Exists(m => !m.IsStatic))
        {
            builder.Services.TryAddScopedSelf<T>();
        }

        foreach (var method in methods)
        {
            builder.Services.AddSingleton(Describe(typeof(T), method));
        }

        return builder;
    }

    private static CodeTool Describe(Type type, MethodInfo method)
    {
        var attribute = method.GetCustomAttribute<AIToolAttribute>()!;
        var name = attribute.Name ?? SnakeCase(method.Name);

        return new CodeTool(
            new ToolDefinition
            {
                // Stable across restarts, and derived from where the method lives rather than from load
                // order, so an agent referring to a code tool keeps referring to the same one.
                Id = "code_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{type.FullName}.{method.Name}")))[..12].ToLowerInvariant(),
                Name = name,
                Description = attribute.Description,
                Kind = ToolKind.Code,
                Safety = attribute.Safety,
                CodeTarget = $"{type.FullName}.{method.Name}",

                // Nothing about a code tool is invoked over HTTP, so the mode and the parameter list that
                // an endpoint tool carries have no meaning here: the method signature is the schema.
                InvocationMode = ToolInvocationMode.InProcess,
                InProcessAllowed = true,
                InProcessAllowedBy = "[AITool]",
            },
            type,
            method);
    }

    /// <summary>Method names are PascalCase; tool names read better to a model in snake_case.</summary>
    internal static string SnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    private static void TryAddScopedSelf<T>(this IServiceCollection services)
        where T : class
    {
        if (!services.Any(d => d.ServiceType == typeof(T)))
        {
            services.AddScoped<T>();
        }
    }
}
