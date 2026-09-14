namespace NetCoreAI;

/// <summary>A saved conversation in the playground or against an agent.</summary>
public sealed record ChatSession
{
    public required string Id { get; init; }
    public string? AgentId { get; init; }
    public string? ModelId { get; init; }
    public string? UserId { get; init; }
    public string Title { get; init; } = "New chat";
    public ModelParameters Parameters { get; init; } = ModelParameters.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Persisted message with usage statistics. Role values follow Microsoft.Extensions.AI ChatRole names.</summary>
public sealed record ChatMessageRecord
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string? ModelId { get; init; }
    /// <summary>Serialized tool calls / results, when any.</summary>
    public string? ToolCallsJson { get; init; }
    /// <summary>Serialized citations for RAG answers.</summary>
    public string? CitationsJson { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public long? LatencyMs { get; init; }
    public decimal? EstimatedCost { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
