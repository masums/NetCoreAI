namespace NetCoreAI;

/// <summary>Where a tool came from, which decides how it is invoked and how much of it can be edited.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ToolKind>))]
public enum ToolKind
{
    /// <summary>One of this host's own endpoints, found by discovery.</summary>
    Endpoint,

    /// <summary>An operation from an imported OpenAPI document.</summary>
    OpenApi,

    /// <summary>A method in the host's code marked <c>[AITool]</c>. Read-only in the designer.</summary>
    Code,

    /// <summary>Written by hand: a URL template and a JSON schema.</summary>
    Manual,
}

/// <summary>How a tool call actually reaches the code behind it.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ToolInvocationMode>))]
public enum ToolInvocationMode
{
    /// <summary>
    /// The host calls itself over the network. The default, and correct by construction: the request goes
    /// through the real pipeline exactly as a browser's would.
    /// </summary>
    HttpLoopback,

    /// <summary>
    /// The endpoint's <c>RequestDelegate</c> is run with a synthetic request carrying the caller's identity.
    /// No socket, and authorization still runs — but only for endpoints someone opted in. See ADR-0004.
    /// </summary>
    InProcess,

    /// <summary>A service somewhere else, reached over HTTP. The only mode an imported OpenAPI tool can use.</summary>
    HttpExternal,
}

/// <summary>Whether a tool changes anything, and what has to happen before it runs.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ToolSafety>))]
public enum ToolSafety
{
    /// <summary>Reads only. Safe to call without asking, and safe to call twice.</summary>
    ReadOnly,

    /// <summary>Changes something. A confirmation policy decides whether it runs unattended.</summary>
    SideEffecting,
}

/// <summary>Who has to agree before a side-effecting tool runs.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ConfirmationPolicy>))]
public enum ConfirmationPolicy
{
    /// <summary>Runs without asking.</summary>
    Auto,

    /// <summary>The caller is asked first; the run pauses until they answer.</summary>
    AskUser,

    /// <summary>Only a caller holding the administrator role may cause it to run at all.</summary>
    AdminOnly,
}

/// <summary>Where a parameter's value comes from when the model is not allowed to supply it.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ParameterBinding>))]
public enum ParameterBinding
{
    /// <summary>The model supplies it. The only kind of parameter the model is told about.</summary>
    Model,

    /// <summary>Bound from a claim on the caller, named by <see cref="ToolParameter.BindingSource"/>.</summary>
    Claim,

    /// <summary>A fixed value the tool always sends.</summary>
    Static,

    /// <summary>Taken from request metadata the host supplied with the agent run.</summary>
    RequestMetadata,
}

/// <summary>Where a parameter rides in the request.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ParameterLocation>))]
public enum ParameterLocation
{
    Route,
    Query,
    Header,
    Body,
}

/// <summary>One parameter of a tool, as the model sees it or as the host fills it in.</summary>
public sealed record ToolParameter
{
    public required string Name { get; init; }

    /// <summary>JSON schema type: string, integer, number, boolean, array, object.</summary>
    public string Type { get; init; } = "string";

    /// <summary>What it means, in the words the model is given. The single biggest influence on tool accuracy.</summary>
    public string? Description { get; init; }

    public bool Required { get; init; }

    public ParameterLocation Location { get; init; } = ParameterLocation.Query;

    /// <summary>
    /// Where the value comes from. Anything other than <see cref="ParameterBinding.Model"/> is hidden from
    /// the model entirely: it is not in the schema the model is shown, so it cannot be set by a tool call.
    /// </summary>
    public ParameterBinding Binding { get; init; } = ParameterBinding.Model;

    /// <summary>Claim type, metadata key or literal value, depending on <see cref="Binding"/>.</summary>
    public string? BindingSource { get; init; }

    /// <summary>Allowed values, when the schema constrains them.</summary>
    public IReadOnlyList<string>? Enum { get; init; }

    public string? Default { get; init; }

    /// <summary>True when the model decides this value, which is what the tool's schema is built from.</summary>
    public bool IsModelSupplied => Binding == ParameterBinding.Model;
}

/// <summary>How much of a response is handed back to the model.</summary>
public sealed record ToolResponseMapping
{
    /// <summary>Dotted path selecting the part worth returning; null returns the whole body.</summary>
    public string? SelectPath { get; init; }

    /// <summary>
    /// Cap on what reaches the model. A response that blows the context window costs the whole turn, and a
    /// tool returning ten thousand rows is a configuration mistake rather than an answer.
    /// </summary>
    public int MaxBytes { get; init; } = 16 * 1024;

    /// <summary>Say so when the response was cut, rather than letting the model read a truncated object as whole.</summary>
    public bool NoteTruncation { get; init; } = true;
}

/// <summary>A tool an agent can call: what it is, how it is invoked, and what the model is allowed to set.</summary>
public sealed record ToolDefinition
{
    public required string Id { get; init; }

    /// <summary>The name the model calls. Letters, digits and underscores; this is what appears in a tool call.</summary>
    public required string Name { get; init; }

    /// <summary>What the tool does and when to use it, written for the model rather than for a developer.</summary>
    public string? Description { get; init; }

    public ToolKind Kind { get; init; } = ToolKind.Endpoint;

    public bool Enabled { get; init; } = true;

    /// <summary>HTTP method for the kinds that have one.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>Route pattern or URL template, e.g. <c>/api/orders/{id}</c> or an absolute URL for external tools.</summary>
    public string? Route { get; init; }

    /// <summary>Base address for <see cref="ToolInvocationMode.HttpExternal"/>; unused by the other modes.</summary>
    public string? BaseUrl { get; init; }

    public ToolInvocationMode InvocationMode { get; init; } = ToolInvocationMode.HttpLoopback;

    /// <summary>
    /// Whether the endpoint was opted in for <see cref="ToolInvocationMode.InProcess"/> — by attribute, or by
    /// an administrator in the designer. In-process invocation is refused without it. See ADR-0004.
    /// </summary>
    public bool InProcessAllowed { get; init; }

    /// <summary>Who enabled in-process invocation, when an administrator did it rather than an attribute.</summary>
    public string? InProcessAllowedBy { get; init; }

    public ToolSafety Safety { get; init; } = ToolSafety.ReadOnly;

    public ConfirmationPolicy Confirmation { get; init; } = ConfirmationPolicy.Auto;

    /// <summary>Forward the caller's own credentials to the endpoint, rather than a service identity.</summary>
    public bool ForwardCallerCredentials { get; init; } = true;

    public IReadOnlyList<ToolParameter> Parameters { get; init; } = [];

    public ToolResponseMapping Response { get; init; } = new();

    /// <summary>Seconds a single call may take before it is abandoned.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>The authorization the source endpoint declared, kept so the designer can show it.</summary>
    public IReadOnlyList<string> RequiredPolicies { get; init; } = [];

    /// <summary>True when the source endpoint allows anonymous callers.</summary>
    public bool AllowsAnonymous { get; init; }

    /// <summary>For <see cref="ToolKind.Code"/>: the method this tool wraps, for display.</summary>
    public string? CodeTarget { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Only what the model supplies: the schema shown to it is built from these alone.</summary>
    public IEnumerable<ToolParameter> ModelParameters => Parameters.Where(p => p.IsModelSupplied);
}

/// <summary>An endpoint discovery found, before anyone has decided to make a tool of it.</summary>
public sealed record DiscoveredEndpoint
{
    /// <summary>Stable across restarts for the same route and method, so a saved tool can be matched back.</summary>
    public required string Id { get; init; }

    public required string Method { get; init; }

    public required string Route { get; init; }

    /// <summary>The endpoint's display name, usually the handler method.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Summary from the OpenAPI document or an XML doc comment, when one is available.</summary>
    public string? Summary { get; init; }

    public IReadOnlyList<ToolParameter> Parameters { get; init; } = [];

    public bool AllowsAnonymous { get; init; }

    public IReadOnlyList<string> RequiredPolicies { get; init; } = [];

    /// <summary>True when the endpoint is marked <c>[AIToolEndpoint]</c> or <c>.WithAITool()</c>.</summary>
    public bool OptedIn { get; init; }

    /// <summary>Name the opt-in asked for, when it named one.</summary>
    public string? SuggestedName { get; init; }

    /// <summary>Id of the tool already built from this endpoint, when there is one.</summary>
    public string? ExistingToolId { get; init; }

    /// <summary>
    /// Why this endpoint cannot become a tool, when it cannot. Shown rather than hiding the row: "the
    /// endpoint I expected is missing" is a worse thing to debug than "here it is, and here is why not".
    /// </summary>
    public string? Unsuitable { get; init; }
}
