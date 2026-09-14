namespace NetCoreAI.Guardrails;

/// <summary>What a rule does when it matches.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<GuardrailAction>))]
public enum GuardrailAction
{
    /// <summary>
    /// Do not look at all. The default, and genuinely free: an agent nobody has written a rule for does
    /// no scanning and its traces carry no guardrail steps.
    /// </summary>
    Ignore,

    /// <summary>Note it in the run trace and carry on. Useful for watching a rule before enforcing it.</summary>
    Report,

    /// <summary>Replace what matched, and carry on with the rest.</summary>
    Mask,

    /// <summary>Stop the run and say why.</summary>
    Block,
}

/// <summary>A kind of personal data a rule can look for.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PiiKind>))]
public enum PiiKind
{
    Email,
    Phone,

    /// <summary>A card number that also passes the Luhn check.</summary>
    CreditCard,

    /// <summary>A US social security number, the shape most commonly asked for.</summary>
    NationalId,

    Iban,
    IpAddress,

    /// <summary>Things that look like credentials: bearer tokens, private key blocks, long secret-shaped strings.</summary>
    Secret,
}

/// <summary>Something a guardrail noticed.</summary>
/// <param name="Rule">Which rule: a PII kind, "injection", "length", or the blocked phrase itself.</param>
/// <param name="Action">What was done about it.</param>
/// <param name="Detail">Enough to explain the decision, without repeating the sensitive part.</param>
public sealed record GuardrailFinding(string Rule, GuardrailAction Action, string? Detail = null);

/// <summary>The result of checking a piece of text.</summary>
/// <param name="Text">The text to carry on with, which may have been masked.</param>
/// <param name="Findings">Everything noticed, in the order the rules ran.</param>
public sealed record GuardrailVerdict(string Text, IReadOnlyList<GuardrailFinding> Findings)
{
    /// <summary>Why the run must stop, or null to continue.</summary>
    public string? BlockedReason { get; init; }

    public bool Blocked => BlockedReason is not null;

    /// <summary>Nothing matched.</summary>
    public static GuardrailVerdict Clean(string text) => new(text, []);
}

/// <summary>Looking for personal data, and what to do about it.</summary>
public sealed record PiiPolicy
{
    /// <summary>
    /// What to do with personal data in what a caller sends. Masking here stops it reaching a remote
    /// model at all, which is the only point at which that is still possible.
    /// </summary>
    public GuardrailAction InputAction { get; init; } = GuardrailAction.Ignore;

    /// <summary>
    /// What to do with personal data in what the model produces. Worth setting even when the input is
    /// clean: the answer can carry data the model read out of a retrieved document.
    /// </summary>
    /// <remarks>
    /// <see cref="GuardrailAction.Block"/> masks here rather than refusing: an answer is produced a piece
    /// at a time and the caller has usually seen the start of it, so replacing what matched is both
    /// possible and more use than discarding the whole thing.
    /// </remarks>
    public GuardrailAction OutputAction { get; init; } = GuardrailAction.Ignore;

    /// <summary>Kinds to look for. Empty means all of them.</summary>
    public IReadOnlyList<PiiKind> Kinds { get; init; } = [];

    /// <summary>Whether this policy looks for <paramref name="kind"/>.</summary>
    public bool Covers(PiiKind kind) => Kinds.Count == 0 || Kinds.Contains(kind);
}

/// <summary>Rules about the text itself, before any model sees it.</summary>
public sealed record ContentPolicy
{
    /// <summary>
    /// Longest message accepted, in characters. A caller who pastes a whole book spends the agent's whole
    /// budget on one run; 0 means no limit.
    /// </summary>
    public int MaxInputCharacters { get; init; }

    /// <summary>Phrases that are refused, matched case-insensitively on a word boundary.</summary>
    public IReadOnlyList<string> BlockedPhrases { get; init; } = [];

    /// <summary>Regular expressions that are refused. An expression that does not compile is ignored, and logged.</summary>
    public IReadOnlyList<string> BlockedPatterns { get; init; } = [];

    /// <summary>What to do when a phrase or pattern matches.</summary>
    public GuardrailAction Action { get; init; } = GuardrailAction.Block;
}

/// <summary>Looking for an attempt to talk the agent out of its instructions.</summary>
public sealed record InjectionPolicy
{
    public bool Enabled { get; init; }

    /// <summary>
    /// How many signals it takes to act. Heuristics of this kind are never certain, so the threshold is
    /// the host's to set: lower catches more and refuses more legitimate questions.
    /// </summary>
    public int Threshold { get; init; } = 2;

    public GuardrailAction Action { get; init; } = GuardrailAction.Block;
}

/// <summary>How much an agent may spend, and over what period.</summary>
/// <remarks>
/// Counted in this process only, like the API key rate limiter. Behind a load balancer, <c>n</c> instances
/// allow <c>n</c> times the budget: a limit on a runaway loop rather than an accounting boundary.
/// </remarks>
public sealed record BudgetPolicy
{
    /// <summary>Tokens one run may use, input and output together. 0 means no limit.</summary>
    public int MaxTokensPerRun { get; init; }

    /// <summary>Tokens one conversation may use in total. 0 means no limit.</summary>
    public int MaxTokensPerSession { get; init; }

    /// <summary>Tokens one caller may use in a rolling 24 hours. 0 means no limit.</summary>
    public int MaxTokensPerUserPerDay { get; init; }

    /// <summary>Estimated cost, in the model's own currency, this agent may run up in a rolling 24 hours. 0 means no limit.</summary>
    public decimal MaxCostPerDay { get; init; }
}

/// <summary>
/// What an agent will not do.
/// </summary>
/// <remarks>
/// Every part is off by default. A guardrail that fires when nobody asked for it turns a working agent
/// into a broken one, and the host is the only party that knows which rules its users can live with.
/// </remarks>
public sealed record GuardrailPolicy
{
    public ContentPolicy Content { get; init; } = new();

    public PiiPolicy Pii { get; init; } = new();

    public InjectionPolicy Injection { get; init; } = new();

    public BudgetPolicy Budget { get; init; } = new();

    /// <summary>
    /// Tools each role may call, by role name. A role that appears here is held to its list; a caller in
    /// no listed role keeps the agent's own tools.
    /// </summary>
    /// <remarks>
    /// An allow-list rather than a deny-list, because the failure modes are not symmetrical: forgetting to
    /// deny a new tool hands it to everyone, while forgetting to allow one only means somebody asks.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ToolsByRole { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the caller is told when a rule blocks their message.</summary>
    public string BlockedMessage { get; init; } = "That request was refused by this agent's rules.";

    /// <summary>Whether any rule here would do anything at all.</summary>
    public bool IsActive =>
        Content.MaxInputCharacters > 0
        || Content.BlockedPhrases.Count > 0
        || Content.BlockedPatterns.Count > 0
        || Injection.Enabled
        || Pii.InputAction != GuardrailAction.Ignore
        || Pii.OutputAction != GuardrailAction.Ignore
        || ToolsByRole.Count > 0
        || Budget.MaxTokensPerRun > 0
        || Budget.MaxTokensPerSession > 0
        || Budget.MaxTokensPerUserPerDay > 0
        || Budget.MaxCostPerDay > 0;
}
