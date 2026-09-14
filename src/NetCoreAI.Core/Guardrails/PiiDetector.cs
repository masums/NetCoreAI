using System.Text.RegularExpressions;

namespace NetCoreAI.Guardrails;

/// <summary>
/// Finding personal data in text, and taking it out.
/// </summary>
/// <remarks>
/// Regular expressions, which means this finds the shapes people write things in and nothing else. It will
/// miss a name, an address, and anything written unusually; it is a reduction in what leaves the process,
/// not a guarantee about what is left. Said plainly here because a masking feature that is quietly
/// believed to be complete is worse than none at all.
/// </remarks>
internal static partial class PiiDetector
{
    // Deliberately conservative. A pattern that also matches ordinary prose turns every answer into
    // brackets, and a host that sees that switches masking off altogether.
    [GeneratedRegex(@"\b[\w.%+\-]+@[\w.\-]+\.[A-Za-z]{2,}\b", RegexOptions.None, 1000)]
    private static partial Regex Email { get; }

    // A card is four to six groups of digits; the Luhn check below throws out the ones that are not.
    [GeneratedRegex(@"\b(?:\d[ \-]?){12,18}\d\b", RegexOptions.None, 1000)]
    private static partial Regex CardLike { get; }

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.None, 1000)]
    private static partial Regex NationalId { get; }

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b", RegexOptions.None, 1000)]
    private static partial Regex Iban { get; }

    // Long enough to be a real number, and anchored so it does not eat the digits of a version string.
    [GeneratedRegex(@"(?<![\w.])(?:\+\d{1,3}[ \-.]?)?(?:\(\d{2,4}\)[ \-.]?)?\d{3,4}[ \-.]\d{3,4}(?:[ \-.]\d{3,4})?(?![\w.])", RegexOptions.None, 1000)]
    private static partial Regex Phone { get; }

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|\b(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\b", RegexOptions.None, 1000)]
    private static partial Regex IpAddress { get; }

    // Credential shapes with a prefix that means something, rather than "any long string", which would
    // match a base64 image, a hash, and half of every stack trace.
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----|\bBearer\s+[A-Za-z0-9\-._~+/]{20,}|\b(?:sk-[A-Za-z0-9]{16,}|ghp_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9\-]{10,})\b", RegexOptions.None, 1000)]
    private static partial Regex Secret { get; }

    /// <summary>
    /// Masks what the policy covers, and says what was found.
    /// </summary>
    /// <remarks>
    /// The order is not arbitrary: a card number is masked before phone numbers are looked for, or a
    /// sixteen-digit card matches the phone pattern first and is reported as the wrong thing.
    /// </remarks>
    public static (string Text, IReadOnlyList<PiiKind> Found) Scan(string text, PiiPolicy policy, bool mask)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrEmpty(text))
        {
            return (text, []);
        }

        var found = new List<PiiKind>();
        var result = text;

        result = Apply(result, PiiKind.Secret, Secret, policy, mask, found, _ => true);
        result = Apply(result, PiiKind.Email, Email, policy, mask, found, _ => true);
        result = Apply(result, PiiKind.CreditCard, CardLike, policy, mask, found, m => Luhn(m.Value));
        result = Apply(result, PiiKind.Iban, Iban, policy, mask, found, _ => true);
        result = Apply(result, PiiKind.NationalId, NationalId, policy, mask, found, _ => true);
        result = Apply(result, PiiKind.IpAddress, IpAddress, policy, mask, found, m => System.Net.IPAddress.TryParse(m.Value, out _));
        result = Apply(result, PiiKind.Phone, Phone, policy, mask, found, m => m.Value.Count(char.IsAsciiDigit) >= 7);

        return (result, found);
    }

    private static string Apply(
        string text,
        PiiKind kind,
        Regex pattern,
        PiiPolicy policy,
        bool mask,
        List<PiiKind> found,
        Func<Match, bool> confirm)
    {
        if (!policy.Covers(kind))
        {
            return text;
        }

        var hit = false;
        var replaced = pattern.Replace(text, match =>
        {
            if (!confirm(match))
            {
                return match.Value;
            }

            hit = true;
            return mask ? Placeholder(kind) : match.Value;
        });

        if (hit)
        {
            found.Add(kind);
        }

        return replaced;
    }

    /// <summary>
    /// What replaces a match.
    /// </summary>
    /// <remarks>
    /// Named rather than blanked out, so the model can still write a sentence about it: "we will email you
    /// at [email address]" reads as an answer, where a row of asterisks reads as a fault.
    /// </remarks>
    private static string Placeholder(PiiKind kind) => kind switch
    {
        PiiKind.Email => "[email address]",
        PiiKind.Phone => "[phone number]",
        PiiKind.CreditCard => "[card number]",
        PiiKind.NationalId => "[national id]",
        PiiKind.Iban => "[bank account]",
        PiiKind.IpAddress => "[ip address]",
        _ => "[secret]",
    };

    /// <summary>The check digit every real card number carries, which is what separates one from any long number.</summary>
    private static bool Luhn(string candidate)
    {
        var sum = 0;
        var second = false;

        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            var c = candidate[i];
            if (!char.IsAsciiDigit(c))
            {
                continue;
            }

            var digit = c - '0';
            if (second)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            second = !second;
        }

        return sum % 10 == 0;
    }
}
