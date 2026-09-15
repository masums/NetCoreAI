using Microsoft.Extensions.Logging;

namespace NetCoreAI.Storage;

/// <summary>Moving agents, tools and knowledge base definitions between hosts.</summary>
public interface IBundleService
{
    /// <summary>
    /// Everything a person built here, or just the agents named.
    /// </summary>
    /// <param name="agentIds">Agents to include, with the tools and knowledge bases they use. Empty exports everything.</param>
    /// <param name="description">What this bundle is for.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    Task<Bundle> ExportAsync(IReadOnlyList<string>? agentIds = null, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>Reads a bundle in, or says what reading it in would do.</summary>
    Task<BundleImportResult> ImportAsync(Bundle bundle, ImportMode mode = ImportMode.Validate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Export and import of what a person built.
/// </summary>
/// <remarks>
/// The unit is the agent, and an export follows its references: the tools it calls, the knowledge bases it
/// reads, the sources those bases pull from. Exporting an agent without them produces a file that imports
/// cleanly and then answers every question slightly wrong.
/// </remarks>
internal sealed class BundleService(
    IMetadataStore store,
    NetCoreAI.Security.IAuditLog audit,
    ILogger<BundleService> logger) : IBundleService
{
    public async Task<Bundle> ExportAsync(
        IReadOnlyList<string>? agentIds = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var allAgents = await store.Agents.ListAsync(cancellationToken).ConfigureAwait(false);
        var agents = agentIds is { Count: > 0 }
            ? [.. allAgents.Where(a => agentIds.Contains(a.Id, StringComparer.OrdinalIgnoreCase))]
            : allAgents;

        var allTools = await store.Tools.ListAsync(cancellationToken).ConfigureAwait(false);
        var allBases = await store.Knowledge.ListAsync(cancellationToken).ConfigureAwait(false);

        // Follow what the chosen agents actually use. Exporting the whole host when somebody asked for one
        // agent hands them a file full of things they did not mean to move.
        var wantedTools = agentIds is { Count: > 0 }
            ? allTools.Where(t => agents.Any(a => a.ToolIds.Contains(t.Id, StringComparer.OrdinalIgnoreCase) || a.ToolIds.Contains(t.Name, StringComparer.OrdinalIgnoreCase))).ToList()
            : [.. allTools];

        var wantedBases = agentIds is { Count: > 0 }
            ? allBases.Where(k => agents.Any(a => a.Knowledge.Any(n => n.KnowledgeBaseId == k.Id))).ToList()
            : [.. allBases];

        var sources = new List<DataSourceDefinition>();
        foreach (var knowledgeBase in wantedBases)
        {
            sources.AddRange(await store.Knowledge.ListSourcesAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false));
        }

        var connections = await store.Connections.ListAsync(cancellationToken).ConfigureAwait(false);

        var bundle = new Bundle
        {
            Description = description,
            Agents = agents,
            Tools = wantedTools,
            KnowledgeBases = wantedBases,
            DataSources = sources,
            Aliases = await store.Aliases.ListAsync(cancellationToken).ConfigureAwait(false),

            // Named so the target host knows what to create, never carried: the secret is the whole of a
            // connection's value to somebody who should not have it.
            Connections = [.. connections.Select(c => new ConnectionReference(c.Id, c.ProviderId) { Name = c.Name, BaseUrl = c.BaseUrl })],
        };

        await audit.WriteAsync(
            NetCoreAI.Security.AuditAction.Downloaded,
            "bundle",
            null,
            description,
            $"{bundle.Agents.Count} agent(s), {bundle.Tools.Count} tool(s), {bundle.KnowledgeBases.Count} knowledge base(s)",
            cancellationToken).ConfigureAwait(false);

        return bundle;
    }

    public async Task<BundleImportResult> ImportAsync(
        Bundle bundle,
        ImportMode mode = ImportMode.Validate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (bundle.FormatVersion > 1)
        {
            throw new NetCoreAIException(
                $"This bundle is version {bundle.FormatVersion} and this NetCoreAI understands version 1. Upgrade, or export it again from a matching version.");
        }

        var created = new List<string>();
        var replaced = new List<string>();
        var skipped = new List<string>();

        // Knowledge bases first, then tools, then agents: an agent's references are checked against what
        // is present by the time it is read, and the order is what makes a one-pass import work.
        foreach (var knowledgeBase in bundle.KnowledgeBases)
        {
            await ApplyAsync(
                $"knowledge base {knowledgeBase.Id}",
                await store.Knowledge.GetAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false) is not null,
                () => store.Knowledge.UpsertAsync(knowledgeBase, cancellationToken),
                mode, created, replaced, skipped).ConfigureAwait(false);
        }

        foreach (var source in bundle.DataSources)
        {
            await ApplyAsync(
                $"data source {source.Id}",
                (await store.Knowledge.ListSourcesAsync(source.KnowledgeBaseId, cancellationToken).ConfigureAwait(false)).Any(s => s.Id == source.Id),
                () => store.Knowledge.UpsertSourceAsync(source, cancellationToken),
                mode, created, replaced, skipped).ConfigureAwait(false);
        }

        foreach (var tool in bundle.Tools)
        {
            await ApplyAsync(
                $"tool {tool.Name}",
                await store.Tools.GetAsync(tool.Id, cancellationToken).ConfigureAwait(false) is not null,

                // Straight to the store rather than through ToolService: a bundle's tools were validated
                // where they were made, and in-process authorisation is an act by a named administrator on
                // this host rather than something a file can carry across.
                () => store.Tools.UpsertAsync(tool with { InProcessAllowed = false, InProcessAllowedBy = null }, cancellationToken),
                mode, created, replaced, skipped).ConfigureAwait(false);
        }

        foreach (var agent in bundle.Agents)
        {
            await ApplyAsync(
                $"agent {agent.Id}",
                await store.Agents.GetAsync(agent.Id, cancellationToken).ConfigureAwait(false) is not null,
                () => store.Agents.UpsertAsync(agent, cancellationToken),
                mode, created, replaced, skipped).ConfigureAwait(false);
        }

        var missing = await MissingAsync(bundle, cancellationToken).ConfigureAwait(false);

        if (mode != ImportMode.Validate)
        {
            logger.LogInformation(
                "Imported a bundle: {Created} created, {Replaced} replaced, {Skipped} skipped, {Missing} unresolved.",
                created.Count, replaced.Count, skipped.Count, missing.Count);

            await audit.WriteAsync(
                NetCoreAI.Security.AuditAction.Uploaded,
                "bundle",
                null,
                bundle.Description,
                $"{created.Count} created, {replaced.Count} replaced, {skipped.Count} skipped",
                cancellationToken).ConfigureAwait(false);
        }

        return new BundleImportResult
        {
            Mode = mode,
            Created = created,
            Replaced = replaced,
            Skipped = skipped,
            Missing = missing,
        };
    }

    private static async Task ApplyAsync(
        string what,
        bool exists,
        Func<Task> write,
        ImportMode mode,
        List<string> created,
        List<string> replaced,
        List<string> skipped)
    {
        if (exists && mode != ImportMode.Overwrite)
        {
            skipped.Add(what);
            return;
        }

        if (mode != ImportMode.Validate)
        {
            await write().ConfigureAwait(false);
        }

        (exists ? replaced : created).Add(what);
    }

    /// <summary>
    /// What the bundle refers to that neither it nor this host has.
    /// </summary>
    /// <remarks>
    /// This is the point of the whole import report. An agent that arrives without its tools runs, answers,
    /// and is quietly wrong; being told "agent support needs tool lookup_order, which is not here" at import
    /// time costs a minute instead of a fortnight.
    /// </remarks>
    private async Task<List<string>> MissingAsync(Bundle bundle, CancellationToken cancellationToken)
    {
        var missing = new List<string>();

        var tools = (await store.Tools.ListAsync(cancellationToken).ConfigureAwait(false))
            .SelectMany(t => new[] { t.Id, t.Name })
            .Concat(bundle.Tools.SelectMany(t => new[] { t.Id, t.Name }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var bases = (await store.Knowledge.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(k => k.Id)
            .Concat(bundle.KnowledgeBases.Select(k => k.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var models = (await store.Models.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(m => m.Id)
            .Concat((await store.Aliases.ListAsync(cancellationToken).ConfigureAwait(false)).Select(a => a.Alias))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var connections = (await store.Connections.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var agent in bundle.Agents)
        {
            foreach (var tool in agent.ToolIds.Where(t => !tools.Contains(t)))
            {
                missing.Add($"agent {agent.Id} needs tool '{tool}'");
            }

            foreach (var knowledge in agent.Knowledge.Where(k => !bases.Contains(k.KnowledgeBaseId)))
            {
                missing.Add($"agent {agent.Id} needs knowledge base '{knowledge.KnowledgeBaseId}'");
            }

            if (!models.Contains(agent.Model))
            {
                missing.Add($"agent {agent.Id} needs model or alias '{agent.Model}'");
            }
        }

        foreach (var knowledgeBase in bundle.KnowledgeBases.Where(k => k.EmbeddingModel is { Length: > 0 } && !models.Contains(k.EmbeddingModel)))
        {
            // Worth its own line: a base whose embedding model is absent cannot be re-indexed, and its
            // existing vectors cannot be compared with anything a different model would produce.
            missing.Add($"knowledge base {knowledgeBase.Id} was indexed with '{knowledgeBase.EmbeddingModel}', which this host does not have");
        }

        foreach (var connection in bundle.Connections.Where(c => !connections.Contains(c.Id)))
        {
            missing.Add($"connection '{connection.Id}' ({connection.ProviderId}) is not set up here — create it and give it its own secret");
        }

        return missing;
    }
}
