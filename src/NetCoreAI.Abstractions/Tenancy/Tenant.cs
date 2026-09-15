namespace NetCoreAI.Tenancy;

/// <summary>
/// What one tenant may use.
/// </summary>
/// <remarks>
/// Every limit is 0 by default, which means no limit. A quota nobody set must not start refusing things
/// the day tenancy is switched on.
/// </remarks>
public sealed record TenantQuota
{
    /// <summary>Agents this tenant may have at once.</summary>
    public int MaxAgents { get; init; }

    /// <summary>Tools this tenant may have at once.</summary>
    public int MaxTools { get; init; }

    /// <summary>Knowledge bases this tenant may have at once.</summary>
    public int MaxKnowledgeBases { get; init; }

    /// <summary>Documents across all of this tenant's knowledge bases.</summary>
    public int MaxDocuments { get; init; }

    /// <summary>Bytes of uploaded files this tenant may keep.</summary>
    public long MaxUploadBytes { get; init; }

    /// <summary>
    /// Tokens this tenant may use in a rolling 24 hours, and the estimated spend it may run up.
    /// </summary>
    /// <remarks>
    /// Counted in this process only, like every other budget here — see
    /// <see cref="NetCoreAI.Guardrails.BudgetPolicy"/>. The counts above are exact, because they are read
    /// from the store; these two bound a runaway loop rather than a bill.
    /// </remarks>
    public long MaxTokensPerDay { get; init; }

    /// <inheritdoc cref="MaxTokensPerDay"/>
    public decimal MaxCostPerDay { get; init; }

    /// <summary>Whether any limit here would do anything at all.</summary>
    public bool IsActive =>
        MaxAgents > 0 || MaxTools > 0 || MaxKnowledgeBases > 0 || MaxDocuments > 0
        || MaxUploadBytes > 0 || MaxTokensPerDay > 0 || MaxCostPerDay > 0;
}

/// <summary>What a tenant is using right now, against what it may use.</summary>
/// <param name="Name">What is being counted, for a person reading it.</param>
/// <param name="Used">The current figure.</param>
/// <param name="Limit">What it may reach, or 0 for no limit.</param>
public readonly record struct QuotaUsage(string Name, long Used, long Limit)
{
    /// <summary>Whether one more would be refused.</summary>
    public bool AtLimit => Limit > 0 && Used >= Limit;
}

/// <summary>One customer of a host that serves several.</summary>
public sealed record Tenant
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// A disabled tenant's requests are refused. Its data is left exactly where it is, because the usual
    /// reason to disable one is a dispute rather than a deletion.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>What this tenant may use. Everything unlimited until somebody says otherwise.</summary>
    public TenantQuota Quota { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>The tenant whose data the current work belongs to.</summary>
/// <remarks>
/// Read by the metadata store on every call, which is what makes isolation something a service cannot
/// forget to apply. A service that had to remember to pass a tenant id would eventually not.
/// </remarks>
public interface ITenantAccessor
{
    /// <summary>The current tenant. Never null: a host that has not switched tenancy on is one tenant.</summary>
    string Current { get; }

    /// <summary>
    /// Runs the rest of this scope as another tenant, until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// For work with no request behind it — a background job, a startup task, an administrator acting on a
    /// tenant's behalf. Not something a request path should call.
    /// </remarks>
    IDisposable Use(string tenantId);
}

/// <summary>Well-known tenant ids.</summary>
public static class TenantId
{
    /// <summary>
    /// The tenant everything belongs to until a host says otherwise.
    /// </summary>
    /// <remarks>
    /// Chosen so a single-tenant host is a multi-tenant host with one tenant, rather than a separate code
    /// path. There is no "tenancy off" branch in the store to get wrong.
    /// </remarks>
    public const string Default = "default";

    /// <summary>Whether this is a usable tenant id.</summary>
    /// <remarks>
    /// Tenant ids end up in storage paths and vector collection names, so the character set is narrow on
    /// purpose: a tenant called <c>../other</c> must not be able to read another's files.
    /// </remarks>
    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && id[0] != '.';
}
