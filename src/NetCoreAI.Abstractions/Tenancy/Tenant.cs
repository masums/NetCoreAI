namespace NetCoreAI.Tenancy;

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
