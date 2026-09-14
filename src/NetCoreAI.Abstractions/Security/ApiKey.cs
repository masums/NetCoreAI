namespace NetCoreAI;

/// <summary>
/// A credential another application holds to reach this host's API.
/// </summary>
/// <remarks>
/// The secret itself is never stored — only a hash of it — so a stolen database does not yield working
/// keys. It is shown once, when the key is made, and cannot be recovered afterwards.
/// </remarks>
public sealed record ApiKey
{
    public required string Id { get; init; }

    /// <summary>What it is for, so a key can be revoked by a person who knows only where it was used.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// SHA-256 of the secret, hex-encoded.
    /// </summary>
    /// <remarks>
    /// A plain hash rather than a password hash, deliberately: the secret is 256 bits of randomness this
    /// host generated, not something a person chose. Slow hashing defends against guessing a weak secret,
    /// and there is no weak secret here to guess — while a slow hash on every API call would be a cost
    /// paid on every request for nothing.
    /// </remarks>
    public required string Hash { get; init; }

    /// <summary>The first few characters, so a key can be recognised in a list without revealing it.</summary>
    public required string Prefix { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Agent ids this key may run. Empty means none: a key with no scope can read but not run, because
    /// the safe reading of "nobody said what this may do" is "nothing".
    /// </summary>
    public IReadOnlyList<string> AgentIds { get; init; } = [];

    /// <summary>Knowledge base ids this key may search. Empty means none.</summary>
    public IReadOnlyList<string> KnowledgeBaseIds { get; init; } = [];

    /// <summary>The wildcard both scope lists accept, meaning "everything this host has".</summary>
    public const string All = "*";

    /// <summary>
    /// Claims the key acts with, as <c>type=value</c>. This is a service identity: whatever is here decides
    /// which documents the key can retrieve and which agents its access tags allow. Granting a claim here
    /// grants everything that claim grants, so it is worth being sparing.
    /// </summary>
    public IReadOnlyList<string> Claims { get; init; } = [];

    /// <summary>Requests a minute before the key is refused. Null means no limit.</summary>
    public int? RateLimitPerMinute { get; init; } = 120;

    /// <summary>
    /// Addresses the key may be used from, as plain addresses or CIDR ranges. Empty means anywhere.
    /// </summary>
    public IReadOnlyList<string> IpAllowList { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the key stops working. Null means it does not expire on its own.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Whether this key may run the named agent.</summary>
    public bool CanRun(string agentId) => Scoped(AgentIds, agentId);

    /// <summary>Whether this key may search the named knowledge base.</summary>
    public bool CanSearch(string knowledgeBaseId) => Scoped(KnowledgeBaseIds, knowledgeBaseId);

    private static bool Scoped(IReadOnlyList<string> allowed, string id) =>
        allowed.Contains(All, StringComparer.Ordinal) || allowed.Contains(id, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A newly created key, with the one and only sight of its secret.</summary>
/// <param name="Key">The stored record.</param>
/// <param name="Secret">The secret to give the holder. Not stored, and not recoverable.</param>
public sealed record CreatedApiKey(ApiKey Key, string Secret);
