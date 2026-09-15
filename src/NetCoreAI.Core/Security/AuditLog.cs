using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Security;

/// <summary>Writing down who changed what, and reading it back.</summary>
public interface IAuditLog
{
    /// <summary>
    /// Records something that happened, attributed to the caller of the current request.
    /// </summary>
    /// <remarks>
    /// Never throws. The operation being recorded has already succeeded, and failing it afterwards because
    /// the log could not be written would be the wrong trade in every case — see the implementation.
    /// </remarks>
    Task WriteAsync(string action, string entityType, string? entityId, string? entityName = null, string? detail = null, CancellationToken cancellationToken = default);

    /// <summary>Records something with the actor given explicitly, for work that happens outside a request.</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ListAsync(AuditFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>How long entries are kept, and whether any are written at all.</summary>
public sealed class AuditOptions
{
    /// <summary>
    /// Record anything. On by default: a host that has to remember to switch its audit log on will
    /// discover it was off at the worst possible moment.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Days entries are kept. 0 keeps them forever, which is a choice a host should make deliberately
    /// rather than inherit — an audit log is the one table nobody notices growing.
    /// </summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>
    /// Record every agent run as well as every change. Off by default: runs already have their own
    /// traces, and on a busy host this doubles the busiest write path to say the same thing twice.
    /// </summary>
    public bool IncludeRuns { get; set; }
}

internal sealed class AuditLog(
    IMetadataStore store,
    IServiceProvider services,
    IOptionsMonitor<AuditOptions> options,
    ILogger<AuditLog> logger) : IAuditLog
{
    public Task WriteAsync(
        string action,
        string entityType,
        string? entityId,
        string? entityName = null,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.CurrentValue.Enabled)
        {
            return Task.CompletedTask;
        }

        // Resolved lazily. A host that never called AddHttpContextAccessor, or work happening on a
        // background thread with no request behind it, must not turn an audit write into an exception.
        var context = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var (kind, id, name) = Actor(context?.User);

        return WriteAsync(
            new AuditEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                EntityName = entityName,
                ActorKind = kind,
                ActorId = id,
                ActorName = name,
                IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
                Detail = detail,
            },
            cancellationToken);
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!options.CurrentValue.Enabled)
        {
            return;
        }

        try
        {
            await store.Audit.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed deliberately, and logged loudly. The thing being recorded has already happened;
            // throwing here would turn "the audit table is full" into "nobody can save an agent", and a
            // host that hits that once switches the audit log off for good. A missing entry is a gap
            // somebody can see in the log. A failed save is an outage.
            logger.LogError(ex, "Could not write the audit entry for {Action} {EntityType} {EntityId}.", entry.Action, entry.EntityType, entry.EntityId);
        }
    }

    public Task<IReadOnlyList<AuditEntry>> ListAsync(AuditFilter filter, CancellationToken cancellationToken = default) =>
        store.Audit.ListAsync(filter, cancellationToken);

    /// <summary>Who the current caller is, as far as the audit log is concerned.</summary>
    internal static (ActorKind Kind, string? Id, string? Name) Actor(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return (ActorKind.Anonymous, null, null);
        }

        var id = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.Identity.Name;

        var name = user.Identity.Name ?? user.FindFirstValue(ClaimTypes.Name) ?? id;

        // An API key is an application, not a person, and the difference matters when reading the log:
        // "the billing service deleted it" and "someone deleted it" call for different next questions.
        var kind = user.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim) is not null
            ? ActorKind.ApiKey
            : ActorKind.User;

        return (kind, id, name);
    }
}
