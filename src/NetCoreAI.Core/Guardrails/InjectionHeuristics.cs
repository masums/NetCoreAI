using System.Text.RegularExpressions;

namespace NetCoreAI.Guardrails;

/// <summary>
/// Signals that a message is trying to talk the agent out of its instructions.
/// </summary>
/// <remarks>
/// Heuristics, and nothing more. Prompt injection is not a pattern but an intent, and intent is expressed
/// in a natural language with unlimited paraphrase; anything here can be worked around by somebody who
/// reads it. The value is that most attempts are not careful, and that a run which trips two of these is
/// worth a host's attention even when it is allowed through. Nothing in the framework relies on this for
/// its safety: what a tool will do is decided by the tool's own rules and the caller's own identity, not
/// by whether the message asking for it looked suspicious.
/// </remarks>
internal static partial class InjectionHeuristics
{
    [GeneratedRegex(@"\b(?:ignore|disregard|forget|override)\b[^.?!]{0,40}\b(?:previous|prior|earlier|above|all)\b[^.?!]{0,20}\b(?:instruction|prompt|rule|direction|message)s?\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Overrule { get; }

    [GeneratedRegex(@"\b(?:reveal|show|print|repeat|output|tell me|what (?:is|are))\b[^.?!]{0,30}\b(?:your|the)\b[^.?!]{0,20}\b(?:system prompt|initial instructions|original instructions|rules|configuration)\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Extract { get; }

    // A message writing its own role markers is trying to look like part of the conversation structure
    // rather than a turn in it.
    [GeneratedRegex(@"(?:^|\n)\s*(?:\[/?(?:INST|SYSTEM)\]|<\|(?:im_start|im_end|system|endoftext)\|>|###\s*(?:system|instruction)|system\s*:)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex RoleMarker { get; }

    [GeneratedRegex(@"\byou are (?:now|no longer)\b|\bfrom now on,? you\b|\bpretend (?:to be|you are)\b|\bact as (?:if you|an? )\b|\bdeveloper mode\b|\bDAN\b|\bjailbreak\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Repersona { get; }

    [GeneratedRegex(@"\b(?:without|bypass|ignor\w*|skip)\b[^.?!]{0,30}\b(?:restriction|filter|guardrail|safety|policy|limitation)s?\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Bypass { get; }

    /// <summary>
    /// What was noticed, one entry per signal. An empty list is not a promise that the message is safe.
    /// </summary>
    public static IReadOnlyList<string> Signals(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var signals = new List<string>();
        Check(Overrule, text, "asks the agent to ignore its instructions", signals);
        Check(Extract, text, "asks the agent to reveal its instructions", signals);
        Check(RoleMarker, text, "contains conversation role markers", signals);
        Check(Repersona, text, "tries to give the agent a different persona", signals);
        Check(Bypass, text, "asks the agent to bypass its restrictions", signals);
        return signals;
    }

    private static void Check(Regex pattern, string text, string description, List<string> signals)
    {
        try
        {
            if (pattern.IsMatch(text))
            {
                signals.Add(description);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A message crafted to make the scan itself expensive is a signal in its own right.
            signals.Add("took too long to scan");
        }
    }
}
