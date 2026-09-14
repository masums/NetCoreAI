using System.Security.Claims;
using System.Text.RegularExpressions;

namespace NetCoreAI.Agents;

/// <summary>
/// Fills <c>{{...}}</c> placeholders in an agent's system prompt.
/// </summary>
/// <remarks>
/// Values come from the host — the caller's claims, metadata the host attached to the run, the agent's own
/// fields. Never from the conversation: a prompt that interpolated the user's message would let anyone
/// rewrite the instructions the agent is running under by typing them.
/// <para>
/// A placeholder with nothing behind it becomes empty rather than being left as literal braces. A model
/// shown <c>{{claims.email}}</c> will cheerfully treat it as the user's address.
/// </para>
/// </remarks>
internal static partial class PromptTemplate
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder { get; }

    public static string Render(
        string template,
        AgentDefinition agent,
        ClaimsPrincipal? user,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal))
        {
            return template;
        }

        return Placeholder.Replace(template, match => Value(match.Groups[1].Value, agent, user, metadata) ?? string.Empty);
    }

    /// <summary>The placeholders a template uses, for showing an author what it depends on.</summary>
    public static IReadOnlyList<string> Placeholders(string? template) =>
        template is null ? [] : [.. Placeholder.Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string? Value(
        string key,
        AgentDefinition agent,
        ClaimsPrincipal? user,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var parts = key.Split('.', 2);
        return parts switch
        {
            ["claims", var claim] => Claim(user, claim),
            ["request", var name] => metadata?.GetValueOrDefault(name),
            ["agent", "name"] => agent.Name,
            ["agent", "description"] => agent.Description,
            ["now", ..] => DateTimeOffset.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture),

            // A bare name is metadata, which is what an author writing {{tenant}} means.
            [var single] => metadata?.GetValueOrDefault(single),
            _ => null,
        };
    }

    /// <summary>Reads a claim by its full type or by the short name an author would write.</summary>
    private static string? Claim(ClaimsPrincipal? user, string type)
    {
        if (user is null)
        {
            return null;
        }

        return user.FindFirst(type)?.Value
            ?? user.Claims.FirstOrDefault(c => Short(c.Type).Equals(type, StringComparison.OrdinalIgnoreCase))?.Value;

        static string Short(string claimType) =>
            claimType.Contains('/', StringComparison.Ordinal) ? claimType[(claimType.LastIndexOf('/') + 1)..] : claimType;
    }
}
