namespace NetCoreAI.Storage;

/// <summary>
/// Agents, tools and knowledge base definitions, in a form that moves between hosts.
/// </summary>
/// <remarks>
/// <para>
/// What a person built, not what a host accumulated. Documents, vectors, conversations, run traces and
/// audit entries are deliberately absent: they belong to the environment they happened in, and carrying a
/// staging server's conversations into production is not a promotion, it is a leak.
/// </para>
/// <para>
/// No secrets, ever — see <see cref="ConnectionReference"/>. A bundle is a file people email each other
/// and commit to repositories, and it is designed on the assumption that they will.
/// </para>
/// </remarks>
public sealed record Bundle
{
    /// <summary>
    /// The shape of this document.
    /// </summary>
    /// <remarks>
    /// Written now so that a bundle made today can be read, or refused with a sentence, by a version that
    /// does not exist yet. A file format without a version is one nobody can change.
    /// </remarks>
    public int FormatVersion { get; init; } = 1;

    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>What this bundle is for, written by whoever made it.</summary>
    public string? Description { get; init; }

    public IReadOnlyList<AgentDefinition> Agents { get; init; } = [];

    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    public IReadOnlyList<KnowledgeBase> KnowledgeBases { get; init; } = [];

    /// <summary>Where each knowledge base gets its documents, so the target can re-ingest rather than copy.</summary>
    public IReadOnlyList<DataSourceDefinition> DataSources { get; init; } = [];

    public IReadOnlyList<ModelAlias> Aliases { get; init; } = [];

    /// <summary>The connections this bundle's contents expect to find, named but never carried.</summary>
    public IReadOnlyList<ConnectionReference> Connections { get; init; } = [];
}

/// <summary>
/// A provider connection an imported thing will look for, described but not carried.
/// </summary>
/// <remarks>
/// The id, the provider and the base URL, and nothing else. A connection's whole value to an attacker is
/// its secret, and a bundle is a file that gets emailed and committed — so the secret is not in it, and
/// there is no field it could be put in by mistake.
/// </remarks>
public sealed record ConnectionReference(string Id, string ProviderId)
{
    public string? Name { get; init; }

    public string? BaseUrl { get; init; }
}

/// <summary>What to do when a bundle names something the host already has.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ImportMode>))]
public enum ImportMode
{
    /// <summary>Report what would happen and write nothing. The sensible first run.</summary>
    Validate,

    /// <summary>Import what is new and leave what exists alone.</summary>
    SkipExisting,

    /// <summary>Import everything, replacing what is already there.</summary>
    Overwrite,
}

/// <summary>What an import did, or would do.</summary>
public sealed record BundleImportResult
{
    public ImportMode Mode { get; init; }

    public IReadOnlyList<string> Created { get; init; } = [];

    public IReadOnlyList<string> Replaced { get; init; } = [];

    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>
    /// Things the bundle refers to that neither it nor this host has.
    /// </summary>
    /// <remarks>
    /// The failure this exists to prevent: an agent imported without its tools looks like it worked and
    /// answers every question slightly wrong, for weeks. Named here rather than discovered later.
    /// </remarks>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>True when nothing was refused and nothing is missing.</summary>
    public bool Clean => Missing.Count == 0;
}
