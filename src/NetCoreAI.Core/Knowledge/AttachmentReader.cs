using Microsoft.Extensions.Logging;

namespace NetCoreAI.Knowledge;

/// <summary>A file read for one conversation turn.</summary>
/// <param name="FileName">The name as it was uploaded, for the model and the transcript.</param>
/// <param name="Text">The extracted text, already capped.</param>
public sealed record Attachment(string FileName, string Text)
{
    /// <summary>Characters the file held before any cap was applied.</summary>
    public int OriginalCharacters { get; init; }

    /// <summary>True when the text was cut to fit. Said, never done quietly.</summary>
    public bool Truncated { get; init; }

    /// <summary>Pages, when the format has them.</summary>
    public int? Pages { get; init; }
}

/// <summary>Reading a file somebody attached to a message.</summary>
public interface IAttachmentReader
{
    /// <summary>File types this host can read, as extensions, for a picker to filter on.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Extracts the text of an uploaded file.
    /// </summary>
    /// <exception cref="NetCoreAIException">Nothing here can read this kind of file.</exception>
    Task<Attachment> ReadAsync(Stream content, string fileName, string? contentType = null, int maxCharacters = 0, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns an attached file into text for one turn of a conversation.
/// </summary>
/// <remarks>
/// <para>
/// Not a knowledge base. An attachment belongs to the message it came with: it is read, put in front of
/// the model once, and forgotten. A file somebody wants answers from repeatedly should be ingested, where
/// it gets chunked, embedded, cited and access-controlled — all of which this deliberately skips.
/// </para>
/// <para>
/// Which sets the limit: the whole text goes into the prompt, so a long document does not fit and cannot
/// be made to. The cap is honest about that rather than letting a 400-page PDF fail as a context error two
/// steps later.
/// </para>
/// </remarks>
internal sealed class AttachmentReader(
    IEnumerable<IDocumentExtractor> extractors,
    ILogger<AttachmentReader> logger) : IAttachmentReader
{
    /// <summary>
    /// Characters kept by default.
    /// </summary>
    /// <remarks>
    /// Roughly 8,000 tokens of English, which leaves room for the conversation and the answer in a
    /// 16k-token context and is not close to the limit of a larger one. A number that fits the smallest
    /// model somebody is likely to have, rather than the largest.
    /// </remarks>
    public const int DefaultMaxCharacters = 32_000;

    private readonly List<IDocumentExtractor> _extractors = [.. extractors.OrderByDescending(e => e.Priority)];

    /// <summary>
    /// The formats worth offering in a file picker.
    /// </summary>
    /// <remarks>
    /// A fixed list filtered by what is actually registered, rather than asked of the extractors — they
    /// answer "can you handle this file", not "what can you handle", and inverting that would mean every
    /// extractor had to keep a second list in step with its first.
    /// </remarks>
    private static readonly string[] Candidates =
        [".txt", ".md", ".csv", ".json", ".log", ".pdf", ".docx", ".pptx", ".xlsx", ".html", ".htm"];

    public IReadOnlyList<string> SupportedExtensions =>
        [.. Candidates.Where(e => _extractors.Exists(x => x.CanHandle("file" + e, null)))];

    public async Task<Attachment> ReadAsync(
        Stream content,
        string fileName,
        string? contentType = null,
        int maxCharacters = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var extractor = _extractors.Find(e => e.CanHandle(fileName, contentType))
            ?? throw new NetCoreAIException(
                $"Nothing here can read '{Path.GetFileName(fileName)}'. This host reads: {string.Join(", ", SupportedExtensions)}."
                + (SupportedExtensions.Contains(".pdf") ? "" : " Add the NetCoreAI.Documents package for PDF and Office files."));

        var document = await extractor.ExtractAsync(content, fileName, contentType, cancellationToken).ConfigureAwait(false);

        // Sections joined with blank lines. A model reading a document is better served by its paragraph
        // breaks than by one wall of text, and the structure costs two characters a section to keep.
        var text = string.Join("\n\n", document.Sections.Select(x => x.Text).Where(t => t is { Length: > 0 }));
        var pages = document.Sections.Select(x => x.Page).OfType<int>().DefaultIfEmpty(0).Max();
        var cap = maxCharacters > 0 ? maxCharacters : DefaultMaxCharacters;

        if (text.Length <= cap)
        {
            return new Attachment(Path.GetFileName(fileName), text)
            {
                OriginalCharacters = text.Length,
                Pages = pages > 0 ? pages : null,
            };
        }

        logger.LogInformation(
            "Attachment {File} is {Length} characters and was cut to {Cap}.", fileName, text.Length, cap);

        return new Attachment(
            Path.GetFileName(fileName),

            // The cut is announced inside the text, where the model will read it. A model handed a
            // document that stops mid-sentence will answer about the part it has as though that were the
            // whole thing, and say so with confidence.
            text[..cap] + $"\n\n[This file was cut off here: it is {text.Length:N0} characters and only the first {cap:N0} were included.]")
        {
            OriginalCharacters = text.Length,
            Truncated = true,
            Pages = pages > 0 ? pages : null,
        };
    }
}
