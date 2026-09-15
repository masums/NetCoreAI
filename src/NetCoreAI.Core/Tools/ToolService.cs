using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Tools;

/// <summary>Creating, editing and removing the tools a model may be given.</summary>
public interface IToolService
{
    Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<ToolDefinition?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a tool, refusing one that is unusable or unsafe as configured.</summary>
    /// <param name="tool">The definition to save.</param>
    /// <param name="allowInProcessBy">
    /// Who is enabling in-process invocation, when an administrator is doing it rather than an attribute.
    /// Recorded on the tool; without it a definition asking for in-process on a non-opted-in endpoint is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the save.</param>
    Task<ToolDefinition> SaveAsync(ToolDefinition tool, string? allowInProcessBy = null, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Builds a tool from one of the host's endpoints, as discovery described it.</summary>
    Task<ToolDefinition> CreateFromEndpointAsync(string endpointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an OpenAPI 3.x document and saves one tool per operation. Existing tools with the same names
    /// are replaced, so re-importing an updated spec is an update rather than a pile of duplicates.
    /// </summary>
    Task<IReadOnlyList<ToolDefinition>> ImportOpenApiAsync(string document, string? baseUrl = null, CancellationToken cancellationToken = default);
}

internal sealed partial class ToolService(
    IMetadataStore store,
    IEndpointDiscovery discovery,
    ICodeToolSource codeTools,
    NetCoreAI.Security.IAuditLog audit,
    ILogger<ToolService> logger) : IToolService
{
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{0,63}$")]
    private static partial Regex NamePattern { get; }

    /// <summary>
    /// Every tool this host has, saved or declared in code.
    /// </summary>
    /// <remarks>
    /// Code tools are listed alongside saved ones because the question a reader is asking — "what can a
    /// model call here?" — does not care which. They are read-only; <see cref="SaveAsync"/> refuses them.
    /// </remarks>
    public async Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        [.. (await store.Tools.ListAsync(cancellationToken).ConfigureAwait(false))
            .Concat(codeTools.Definitions)
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    public async Task<ToolDefinition?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        await store.Tools.GetAsync(id, cancellationToken).ConfigureAwait(false)
        ?? codeTools.Definitions.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task<ToolDefinition> SaveAsync(ToolDefinition tool, string? allowInProcessBy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Kind == ToolKind.Code)
        {
            // A code tool is a view of a method the compiler owns. Saving a row for it would create a copy
            // that the next deployment silently disagrees with.
            throw new NetCoreAIException(
                $"'{tool.Name}' is defined in code with [AITool]. Change the method, not this: code tools are read-only here.");
        }

        if (codeTools.Definitions.Any(c => string.Equals(c.Name, tool.Name, StringComparison.Ordinal)))
        {
            throw new NetCoreAIException($"A code tool is already called '{tool.Name}'. The model calls tools by name, so two cannot share one.");
        }

        if (!NamePattern.IsMatch(tool.Name))
        {
            // The name is what appears in a tool call, and providers reject anything else.
            throw new NetCoreAIException(
                $"'{tool.Name}' is not a usable tool name. Use a letter followed by letters, digits or underscores, up to 64 characters.");
        }

        if (await store.Tools.GetByNameAsync(tool.Name, cancellationToken).ConfigureAwait(false) is { } clash && clash.Id != tool.Id)
        {
            throw new NetCoreAIException($"Another tool is already called '{tool.Name}'. The model calls tools by name, so two cannot share one.");
        }

        var saved = tool with { UpdatedAt = DateTimeOffset.UtcNow };
        saved = AuthorizeInProcess(saved, allowInProcessBy);
        ValidateParameters(saved);

        var existed = await store.Tools.GetAsync(saved.Id, cancellationToken).ConfigureAwait(false) is not null;
        await store.Tools.UpsertAsync(saved, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Saved tool {Name} ({Kind}, {Mode}).", saved.Name, saved.Kind, saved.InvocationMode);

        await audit.WriteAsync(
            existed ? NetCoreAI.Security.AuditAction.Updated : NetCoreAI.Security.AuditAction.Created,
            NetCoreAI.Security.AuditEntity.Tool,
            saved.Id,
            saved.Name,

            // In-process is the setting worth seeing in a log without opening the tool: it is the one that
            // decides whether a model's call runs inside this host.
            saved.InvocationMode == ToolInvocationMode.InProcess ? "runs in-process" : null,
            cancellationToken).ConfigureAwait(false);

        return saved;
    }

    /// <summary>
    /// Decides whether this definition may use in-process invocation, per ADR-0004.
    /// </summary>
    /// <remarks>
    /// In-process runs the endpoint with a synthetic request, which is close enough to a real one for an
    /// endpoint somebody examined and not close enough for one nobody has. Enabling it is therefore always
    /// an act by a named person or an attribute, and an administrator's act is recorded on the tool.
    /// </remarks>
    private ToolDefinition AuthorizeInProcess(ToolDefinition tool, string? allowInProcessBy)
    {
        if (tool.InvocationMode != ToolInvocationMode.InProcess)
        {
            return tool;
        }

        if (tool.Kind is not ToolKind.Endpoint)
        {
            throw new NetCoreAIException(
                $"Only tools built from this host's own endpoints can run in-process; '{tool.Name}' is {tool.Kind}. Use HTTP instead.");
        }

        if (tool.InProcessAllowed && tool.InProcessAllowedBy is { Length: > 0 })
        {
            return tool;
        }

        var endpoint = discovery.Discover().FirstOrDefault(e => e.Id == ToolEndpointId(tool));
        if (endpoint?.OptedIn == true)
        {
            return tool with { InProcessAllowed = true, InProcessAllowedBy = "[AIToolEndpoint]" };
        }

        if (allowInProcessBy is not { Length: > 0 })
        {
            throw new NetCoreAIException(
                $"'{tool.Name}' asks to run in-process, but its endpoint is not marked [AIToolEndpoint] or .WithAITool(). Mark it in code, or enable it here as an administrator. Until then it runs over HTTP.");
        }

        logger.LogWarning("In-process invocation enabled for tool {Name} ({Method} {Route}) by {User}.", tool.Name, tool.Method, tool.Route, allowInProcessBy);
        return tool with { InProcessAllowed = true, InProcessAllowedBy = allowInProcessBy };
    }

    private static string ToolEndpointId(ToolDefinition tool) =>
        DiscoveredEndpoint.IdFor(tool.Method, tool.Route ?? "/");

    private static void ValidateParameters(ToolDefinition tool)
    {
        foreach (var parameter in tool.Parameters)
        {
            if (parameter.Binding is ParameterBinding.Claim or ParameterBinding.RequestMetadata or ParameterBinding.Static
                && parameter.BindingSource is not { Length: > 0 })
            {
                // A locked parameter with nothing to bind from would silently send null, which for a
                // tenant id is the difference between "this tenant" and "all of them".
                throw new NetCoreAIException(
                    $"Parameter '{parameter.Name}' is bound from {parameter.Binding} but names no source. Give the claim type, metadata key or value it takes.");
            }
        }

        if (tool.Parameters.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tool.Parameters.Count)
        {
            throw new NetCoreAIException($"'{tool.Name}' has two parameters with the same name.");
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        // Read before deleting, so the audit entry can carry the name. Afterwards there is nothing to
        // look it up from, and "tool 7f3a… was deleted" answers nobody's question.
        var tool = await store.Tools.GetAsync(id, cancellationToken).ConfigureAwait(false);
        await store.Tools.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(NetCoreAI.Security.AuditAction.Deleted, NetCoreAI.Security.AuditEntity.Tool, id, tool?.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolDefinition> CreateFromEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        var endpoint = discovery.Discover().FirstOrDefault(e => e.Id == endpointId)
            ?? throw new NetCoreAIException($"No endpoint with id '{endpointId}' is routed by this host. The route table may have changed since the list was loaded.");

        if (endpoint.Unsuitable is { Length: > 0 } reason)
        {
            throw new NetCoreAIException($"{endpoint.Method} {endpoint.Route} cannot become a tool: {reason}");
        }

        var tool = new ToolDefinition
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = await UniqueNameAsync(endpoint.SuggestedName ?? endpoint.Id, cancellationToken).ConfigureAwait(false),
            Description = endpoint.Summary,
            Kind = ToolKind.Endpoint,
            Method = endpoint.Method,
            Route = endpoint.Route,
            Parameters = endpoint.Parameters,
            RequiredPolicies = endpoint.RequiredPolicies,
            AllowsAnonymous = endpoint.AllowsAnonymous,

            // Opting in says "safe to expose", not "safe to modify anything": a GET is read-only and
            // everything else is assumed to change something until a person says otherwise.
            Safety = endpoint.Method is "GET" ? ToolSafety.ReadOnly : ToolSafety.SideEffecting,

            // Loopback even for an opted-in endpoint. In-process is faster, but it is a choice someone
            // makes in the designer rather than something that happens because an attribute was present.
            InvocationMode = ToolInvocationMode.HttpLoopback,
            InProcessAllowed = endpoint.OptedIn,
            InProcessAllowedBy = endpoint.OptedIn ? "[AIToolEndpoint]" : null,
        };

        return await SaveAsync(tool, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolDefinition>> ImportOpenApiAsync(string document, string? baseUrl = null, CancellationToken cancellationToken = default)
    {
        var parsed = OpenApiImport.Read(document, baseUrl);
        var saved = new List<ToolDefinition>(parsed.Count);

        foreach (var tool in parsed)
        {
            // Re-importing an updated spec should be an update, not a second copy of every operation.
            var existing = await store.Tools.GetByNameAsync(tool.Name, cancellationToken).ConfigureAwait(false);
            saved.Add(await SaveAsync(
                tool with { Id = existing?.Id ?? Guid.NewGuid().ToString("N")[..12], CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow },
                cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        logger.LogInformation("Imported {Count} operation(s) from an OpenAPI document.", saved.Count);
        return saved;
    }

    /// <summary>A name nothing else is using, so importing a spec twice does not fail on the first clash.</summary>
    private async Task<string> UniqueNameAsync(string preferred, CancellationToken cancellationToken)
    {
        var name = preferred;
        for (var suffix = 2; suffix < 100; suffix++)
        {
            if (await store.Tools.GetByNameAsync(name, cancellationToken).ConfigureAwait(false) is null)
            {
                return name;
            }

            name = $"{preferred}_{suffix}";
        }

        return $"{preferred}_{Guid.NewGuid():N}"[..60];
    }
}
