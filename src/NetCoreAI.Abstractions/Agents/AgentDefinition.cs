namespace NetCoreAI;

/// <summary>What an agent is asked to produce.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AgentOutputMode>))]
public enum AgentOutputMode
{
    /// <summary>Prose, as a person would read it.</summary>
    Text,

    /// <summary>JSON matching <see cref="AgentDefinition.OutputSchema"/>.</summary>
    Json,
}

/// <summary>How much of a conversation an agent is reminded of.</summary>
public sealed record MemoryPolicy
{
    /// <summary>
    /// Most recent turns kept. A window rather than the whole history: context costs money on every call,
    /// and a long conversation would otherwise grow until it stopped fitting.
    /// </summary>
    public int WindowTurns { get; init; } = 10;

    /// <summary>Remember anything at all. Off makes every run independent, which suits a one-shot agent.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>A knowledge base attached to an agent, with the retrieval settings it should use.</summary>
/// <param name="KnowledgeBaseId">The base to search.</param>
public sealed record AgentKnowledge(string KnowledgeBaseId)
{
    /// <summary>Overrides the base's own defaults for this agent; null uses them.</summary>
    public RetrievalOptions? Retrieval { get; init; }
}

/// <summary>Limits on how hard an agent may try before it is stopped.</summary>
public sealed record AgentLimits
{
    /// <summary>
    /// Tool-calling rounds before the run is stopped. A model that has misunderstood a tool will call it
    /// again with slightly different arguments for as long as it is allowed to, so this is a real bound
    /// rather than a formality.
    /// </summary>
    public int MaxToolIterations { get; init; } = 8;

    /// <summary>Seconds any one tool call may take.</summary>
    public int ToolTimeoutSeconds { get; init; } = 30;

    /// <summary>Seconds the whole run may take, including retrieval, tool calls and generation.</summary>
    public int RunTimeoutSeconds { get; init; } = 300;
}

/// <summary>An agent: a model, a prompt, the tools it may call and the documents it may read.</summary>
public sealed record AgentDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>What it is for, for the people choosing between agents. Not shown to the model.</summary>
    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Model id or alias. Falls back through <see cref="FallbackModels"/> when it cannot serve.</summary>
    public string Model { get; init; } = ModelAlias.Default;

    /// <summary>Tried in order when the primary model fails, so an outage degrades rather than stops.</summary>
    public IReadOnlyList<string> FallbackModels { get; init; } = [];

    /// <summary>
    /// The system prompt, which may carry placeholders: <c>{{claims.email}}</c>, <c>{{request.tenant}}</c>,
    /// <c>{{agent.name}}</c>. Values come from the host, never from the conversation.
    /// </summary>
    public string? SystemPrompt { get; init; }

    public ModelParameters Parameters { get; init; } = new();

    /// <summary>Tool ids or names this agent may call. Nothing else is offered to it.</summary>
    public IReadOnlyList<string> ToolIds { get; init; } = [];

    public IReadOnlyList<AgentKnowledge> Knowledge { get; init; } = [];

    public MemoryPolicy Memory { get; init; } = new();

    public AgentLimits Limits { get; init; } = new();

    /// <summary>
    /// What this agent will not do: content rules, PII masking, injection heuristics, per-role tool
    /// allow-lists and budgets. Null uses the host's default from <c>NetCoreAIOptions.Guardrails</c>.
    /// </summary>
    public NetCoreAI.Guardrails.GuardrailPolicy? Guardrails { get; init; }

    public AgentOutputMode OutputMode { get; init; } = AgentOutputMode.Text;

    /// <summary>JSON schema the answer must match, when <see cref="OutputMode"/> is JSON.</summary>
    public string? OutputSchema { get; init; }

    /// <summary>
    /// Access tags a caller must hold to run this agent. Empty means anyone who reaches the API may.
    /// </summary>
    public IReadOnlyList<string> AclTags { get; init; } = [];

    /// <summary>
    /// The version currently serving runs, or null while this agent has never been published.
    /// </summary>
    /// <remarks>
    /// An agent nobody has published runs as it is edited, which is what a draft should do and what every
    /// agent did before versioning existed. Publishing once changes that for good: from then on this
    /// record is the draft, and runs use the published version until somebody publishes again. Opting in
    /// is the act of publishing rather than a setting, because a flag nobody finds is a feature nobody has.
    /// </remarks>
    public int? PublishedVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>One thing that happened during a run, in the order it happened.</summary>
/// <param name="Kind">"retrieval", "tool", "generation", "guardrail" or "error".</param>
/// <param name="Name">What it acted on: a knowledge base id, a tool name, a model id.</param>
public sealed record RunStep(string Kind, string Name)
{
    public const string RetrievalKind = "retrieval";
    public const string ToolKind = "tool";
    public const string GenerationKind = "generation";
    public const string ErrorKind = "error";

    /// <summary>A rule that noticed something, whether or not it stopped the run.</summary>
    public const string GuardrailKind = "guardrail";

    /// <summary>Arguments a tool was called with, as the model chose them.</summary>
    public string? Input { get; init; }

    /// <summary>What came back, truncated to what is worth storing.</summary>
    public string? Output { get; init; }

    public bool Success { get; init; } = true;

    public long ElapsedMs { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Everything one run of an agent did, kept so a wrong answer can be explained.</summary>
public sealed record RunTrace
{
    public required string Id { get; init; }

    public required string AgentId { get; init; }

    public string? SessionId { get; init; }

    /// <summary>Who ran it, when there was a user behind it.</summary>
    public string? UserId { get; init; }

    public string? Input { get; init; }

    public string? Output { get; init; }

    public string? ModelId { get; init; }

    public IReadOnlyList<RunStep> Steps { get; init; } = [];

    public IReadOnlyList<Citation> Citations { get; init; } = [];

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public decimal? EstimatedCost { get; init; }

    public long ElapsedMs { get; init; }

    public bool Success { get; init; } = true;

    /// <summary>Why the run failed, or why it stopped early.</summary>
    public string? Error { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A request to run an agent.</summary>
public sealed record AgentRequest
{
    public required string Message { get; init; }

    /// <summary>Continues a conversation; null starts one.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Values the host attaches to this run, available to the prompt as <c>{{request.x}}</c> and to tool
    /// parameters bound from request metadata. From the host, never from the caller's message.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>What an agent produced.</summary>
public sealed record AgentResponse
{
    public required string Text { get; init; }

    public required string RunId { get; init; }

    public string? SessionId { get; init; }

    public IReadOnlyList<Citation> Citations { get; init; } = [];

    /// <summary>Tool calls made, in order, for a caller that wants to show its work.</summary>
    public IReadOnlyList<RunStep> Steps { get; init; } = [];

    public string? ModelId { get; init; }

    public long ElapsedMs { get; init; }

    /// <summary>Set when the run ended early — a limit reached, or a model that could not be served.</summary>
    public string? Error { get; init; }
}

/// <summary>Running agents from application code.</summary>
public interface IAgentClient
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs an agent and waits for the whole answer.</summary>
    Task<AgentResponse> RunAsync(string agentId, AgentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs an agent, yielding text as it is produced.</summary>
    IAsyncEnumerable<AgentEvent> RunStreamingAsync(string agentId, AgentRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A unit of a streaming run.</summary>
/// <param name="Type">"delta", "citations", "step", "done" or "error".</param>
public sealed record AgentEvent(string Type)
{
    public const string DeltaType = "delta";
    public const string CitationsType = "citations";
    public const string StepType = "step";
    public const string DoneType = "done";
    public const string ErrorType = "error";

    public string? Text { get; init; }

    public IReadOnlyList<Citation>? Citations { get; init; }

    /// <summary>A tool call or retrieval as it happens, so a playground can show work in progress.</summary>
    public RunStep? Step { get; init; }

    public AgentResponse? Response { get; init; }

    public string? Error { get; init; }
}
