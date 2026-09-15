using Microsoft.Extensions.Logging;

namespace NetCoreAI.Tenancy;

/// <summary>Creating, listing and disabling tenants.</summary>
public interface ITenantService
{
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default);

    Task<Tenant?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default);

    Task<Tenant> UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default);
}

/// <summary>
/// The current tenant, held in an <see cref="AsyncLocal{T}"/>.
/// </summary>
/// <remarks>
/// Ambient rather than passed, because the alternative is threading a tenant id through every method of
/// every service, and the one call that forgets is a data leak rather than a compile error. An
/// <c>AsyncLocal</c> follows the request across every await in it, including into background work started
/// from it — which is the behaviour wanted here, since that work is still the tenant's.
/// </remarks>
internal sealed class TenantAccessor : ITenantAccessor
{
    private static readonly AsyncLocal<string?> Value = new();

    public string Current => Value.Value ?? TenantId.Default;

    public IDisposable Use(string tenantId)
    {
        if (!TenantId.IsValid(tenantId))
        {
            throw new NetCoreAIException($"'{tenantId}' is not a usable tenant id: use letters, digits, dashes, underscores or dots, up to 64 characters.");
        }

        var previous = Value.Value;
        Value.Value = tenantId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                Value.Value = previous;
            }
        }
    }
}

/// <summary>
/// Tenants, stored as settings rather than in a table of their own.
/// </summary>
/// <remarks>
/// The tenant list is the one thing that cannot itself be tenant-scoped, and the settings store is already
/// the host-wide one. A handful of rows in a key-value table is the right size for something read once per
/// request and written when somebody signs up.
/// </remarks>
internal sealed class TenantService(IMetadataStore store, ILogger<TenantService> logger) : ITenantService
{
    private const string Prefix = "tenant:";

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default)
    {
        var settings = await store.Settings.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<Tenant>();

        foreach (var (key, value) in settings)
        {
            if (key.StartsWith(Prefix, StringComparison.Ordinal)
                && Deserialize(value) is { } tenant)
            {
                tenants.Add(tenant);
            }
        }

        if (tenants.Count == 0)
        {
            // The default tenant exists whether or not anybody wrote it down, so a host that switches
            // tenancy on does not find its existing data belonging to nobody.
            tenants.Add(new Tenant { Id = TenantId.Default, Name = "Default" });
        }

        return [.. tenants.OrderBy(t => t.Id, StringComparer.Ordinal)];
    }

    public async Task<Tenant?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var value = await store.Settings.GetAsync(Prefix + id, cancellationToken).ConfigureAwait(false);
        if (value is { Length: > 0 } && Deserialize(value) is { } tenant)
        {
            return tenant;
        }

        return id == TenantId.Default ? new Tenant { Id = TenantId.Default, Name = "Default" } : null;
    }

    public async Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (!TenantId.IsValid(tenant.Id))
        {
            throw new NetCoreAIException($"'{tenant.Id}' is not a usable tenant id: use letters, digits, dashes, underscores or dots, up to 64 characters.");
        }

        if (await store.Settings.GetAsync(Prefix + tenant.Id, cancellationToken).ConfigureAwait(false) is { Length: > 0 })
        {
            throw new NetCoreAIException($"A tenant with id '{tenant.Id}' already exists.");
        }

        await SaveAsync(tenant, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Created tenant {Id} ({Name}).", tenant.Id, tenant.Name);
        return tenant;
    }

    public async Task<Tenant> UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await SaveAsync(tenant, cancellationToken).ConfigureAwait(false);
        return tenant;
    }

    private Task SaveAsync(Tenant tenant, CancellationToken cancellationToken) =>
        store.Settings.SetAsync(
            Prefix + tenant.Id,
            System.Text.Json.JsonSerializer.Serialize(tenant),
            cancellationToken);

    private static Tenant? Deserialize(string value)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Tenant>(value);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
