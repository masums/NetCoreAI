namespace NetCoreAI.Security;

/// <summary>What kind of caller did something.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ActorKind>))]
public enum ActorKind
{
    /// <summary>Nobody was signed in. Either the dashboard is open, or this ran outside a request.</summary>
    Anonymous,

    /// <summary>A signed-in person.</summary>
    User,

    /// <summary>An API key, which is an application rather than a person.</summary>
    ApiKey,

    /// <summary>NetCoreAI itself: a scheduled job, a retention sweep, startup.</summary>
    System,
}

/// <summary>One thing that happened, and who caused it.</summary>
/// <remarks>
/// Written after the fact, never in the way of the operation. An audit log that can fail a save is a log
/// that gets turned off the first time it does.
/// </remarks>
public sealed record AuditEntry
{
    public required string Id { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>What was done: see <see cref="AuditAction"/>.</summary>
    public required string Action { get; init; }

    /// <summary>What it was done to: see <see cref="AuditEntity"/>.</summary>
    public required string EntityType { get; init; }

    public string? EntityId { get; init; }

    /// <summary>
    /// The name at the time. Kept as a copy rather than looked up later, because half the point of an
    /// audit log is explaining something that has since been deleted.
    /// </summary>
    public string? EntityName { get; init; }

    public ActorKind ActorKind { get; init; } = ActorKind.Anonymous;

    /// <summary>The caller's stable id — a user id, or the id of the API key.</summary>
    public string? ActorId { get; init; }

    /// <summary>What to show: a display name, or the key's name.</summary>
    public string? ActorName { get; init; }

    /// <summary>Where the request came from, when there was one.</summary>
    public string? IpAddress { get; init; }

    /// <summary>One sentence about what changed. Not a diff: a diff of secrets is a copy of them.</summary>
    public string? Detail { get; init; }

    public bool Success { get; init; } = true;
}

/// <summary>The actions NetCoreAI records. Hosts may write their own.</summary>
public static class AuditAction
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";

    /// <summary>An agent was run. The run's own trace holds what it did; this holds who asked.</summary>
    public const string Ran = "ran";

    public const string Tested = "tested";
    public const string Uploaded = "uploaded";
    public const string Downloaded = "downloaded";
    public const string Refused = "refused";
}

/// <summary>The things NetCoreAI records actions against.</summary>
public static class AuditEntity
{
    public const string Model = "model";
    public const string Connection = "connection";
    public const string Tool = "tool";
    public const string Agent = "agent";
    public const string KnowledgeBase = "knowledge-base";
    public const string Document = "document";
    public const string ApiKey = "api-key";
    public const string Setting = "setting";
}

/// <summary>Which entries to return.</summary>
public sealed record AuditFilter
{
    public string? EntityType { get; init; }

    public string? EntityId { get; init; }

    public string? ActorId { get; init; }

    public string? Action { get; init; }

    public DateTimeOffset? Since { get; init; }

    public int Limit { get; init; } = 100;
}
