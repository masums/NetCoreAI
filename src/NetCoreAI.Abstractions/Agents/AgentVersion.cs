namespace NetCoreAI;

/// <summary>
/// An agent as it was when somebody published it.
/// </summary>
/// <remarks>
/// Append-only. A rollback publishes an old definition again as a new version rather than deleting the
/// ones after it, so the history stays a record of what happened rather than a record of what somebody
/// would now prefer to have happened.
/// </remarks>
public sealed record AgentVersion
{
    public required string AgentId { get; init; }

    /// <summary>1 for the first publish, counting up. Never reused, including after a rollback.</summary>
    public required int Version { get; init; }

    /// <summary>The agent exactly as it was. What runs, until a later version is published.</summary>
    public required AgentDefinition Definition { get; init; }

    /// <summary>What changed and why, written by whoever published it.</summary>
    public string? Note { get; init; }

    /// <summary>The version this one restored, when it was a rollback.</summary>
    public int? RolledBackFrom { get; init; }

    public string? PublishedBy { get; init; }

    public DateTimeOffset PublishedAt { get; init; } = DateTimeOffset.UtcNow;
}
