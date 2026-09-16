using System.Text;

namespace NetCoreAI.Guardrails;

/// <summary>
/// Masks an answer while it is still being written.
/// </summary>
/// <remarks>
/// Streaming and masking pull against each other: a card number arrives as several deltas, and a rule
/// applied to each delta on its own sees four ordinary digits four times and passes them all. So text is
/// held back until enough of it has arrived for a match to be impossible across the seam — a fixed
/// look-behind, cut at a space so words are not split — and only then masked and released. The caller sees
/// the answer a fraction of a second later than they otherwise would, which is the price of the rule
/// working at all. When nothing is being masked, deltas pass straight through and nothing is held.
/// </remarks>
internal sealed class StreamingOutputGuard(IGuardrailService guardrails, GuardrailPolicy policy)
{
    /// <summary>
    /// Characters kept back. Comfortably longer than anything the detector matches, so a run of held text
    /// can never contain the start of a pattern whose end has already been released.
    /// </summary>
    private const int HoldBack = 96;

    private readonly StringBuilder _buffer = new();
    private readonly bool _active = policy.Pii.OutputAction is GuardrailAction.Mask or GuardrailAction.Block;
    private readonly List<GuardrailFinding> _findings = [];

    /// <summary>Everything the rules noticed while the answer was written.</summary>
    public IReadOnlyList<GuardrailFinding> Findings => _findings;

    /// <summary>Accepts a delta and returns what may be shown now, which may be nothing.</summary>
    public string Push(string delta)
    {
        if (!_active)
        {
            return delta;
        }

        _buffer.Append(delta);
        if (_buffer.Length <= HoldBack)
        {
            return "";
        }

        var text = _buffer.ToString();
        var cut = text.LastIndexOf(' ', text.Length - HoldBack);
        if (cut <= 0)
        {
            return "";
        }

        _buffer.Remove(0, cut + 1);
        return Clean(text[..(cut + 1)]);
    }

    /// <summary>Releases what is still held, once the answer is complete.</summary>
    public string Flush()
    {
        if (!_active || _buffer.Length == 0)
        {
            return "";
        }

        var text = _buffer.ToString();
        _buffer.Clear();
        return Clean(text);
    }

    private string Clean(string text)
    {
        var verdict = guardrails.CheckOutput(policy, text);
        _findings.AddRange(verdict.Findings);
        return verdict.Text;
    }
}
