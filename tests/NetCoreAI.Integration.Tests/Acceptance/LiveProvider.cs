using System.Runtime.InteropServices;

namespace NetCoreAI.Integration.Tests.Acceptance;

/// <summary>A real provider the acceptance tests can run against, when its key is configured.</summary>
/// <param name="Name">How it appears in test output.</param>
/// <param name="BaseUrl">An OpenAI-compatible endpoint.</param>
/// <param name="Key">The secret, never logged.</param>
/// <param name="Model">The model id to ask for.</param>
public sealed record LiveProvider(string Name, string BaseUrl, string Key, string Model)
{
    /// <summary>
    /// Whether this provider can carry a conversation on after a tool call.
    /// </summary>
    /// <remarks>
    /// Gemini cannot, through its OpenAI-compatible endpoint. Its models return a
    /// <c>thought_signature</c> inside each tool call's <c>extra_content</c> and require it to be sent
    /// back on the next turn; <c>Microsoft.Extensions.AI</c>'s OpenAI adapter maps responses onto its own
    /// types and drops the vendor extension, so the follow-up request is refused with
    /// "Function call is missing a thought_signature in functionCall parts".
    /// <para>
    /// Single-turn chat and the first half of a tool call both work. The limitation is checked by
    /// <c>GeminiToolLimitationTests</c>, which fails if it ever stops being true — so this flag gets
    /// removed when the world changes rather than outliving the problem.
    /// </para>
    /// </remarks>
    public bool MultiTurnTools { get; init; } = true;

    /// <summary>
    /// Every provider this machine has a key for.
    /// </summary>
    /// <remarks>
    /// Running the same acceptance against more than one real provider is the point: a single provider
    /// hides the assumptions made about it. Gemini, for instance, returns an extra <c>extra_content</c>
    /// field inside a tool call that OpenAI does not — something no fake would have shown.
    /// </remarks>
    public static IReadOnlyList<LiveProvider> Available =>
    [
        .. new[]
        {
            Make("OpenRouter", "https://openrouter.ai/api/v1", "OPENROUTER_FREE_KEY", "OPENROUTER_FREE_MODEL", "openrouter/free"),
            Make("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_FREE_KEY", "GEMINI_FREE_MODEL", "gemini-flash-latest") is { } gemini
                ? gemini with { MultiTurnTools = false }
                : null,
        }.OfType<LiveProvider>(),
    ];

    private static LiveProvider? Make(string name, string baseUrl, string keyVariable, string modelVariable, string defaultModel) =>
        Read(keyVariable) is { Length: > 0 } key
            ? new LiveProvider(name, baseUrl, key, Read(modelVariable) ?? defaultModel)
            : null;

    /// <summary>
    /// Reads a variable from the process environment, then the user's.
    /// </summary>
    /// <remarks>
    /// A variable set with <c>setx</c> or the Windows settings dialog does not reach a process that was
    /// already running, which is the commonest reason a developer's "I set it" and a test's "it is not
    /// set" are both true at once.
    /// </remarks>
    private static string? Read(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            : null);
}
