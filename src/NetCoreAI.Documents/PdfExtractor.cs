using System.Text;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace NetCoreAI.Documents;

/// <summary>
/// PDF text extraction with PdfPig, one section per page.
/// </summary>
/// <remarks>
/// The page number is what makes a citation useful — "page 214 of the handbook" rather than "somewhere in
/// the handbook" — so each page becomes its own section and the page number travels with every chunk cut
/// from it. A scanned PDF has no text layer and produces nothing; that is reported as needing OCR rather
/// than as an empty document, because the two need different actions from the user.
/// </remarks>
public sealed class PdfExtractor(ILogger<PdfExtractor> logger) : IDocumentExtractor
{
    /// <summary>Ahead of the generic extractors, which would otherwise claim a PDF by content type.</summary>
    public int Priority => 10;

    public bool CanHandle(string fileName, string? contentType) =>
        Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        || contentType is "application/pdf";

    public Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // PdfPig needs a seekable stream and reads synchronously; copying first keeps the caller's stream
        // simple (a network or upload stream is not seekable).
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        buffer.Position = 0;

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(buffer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new NetCoreAIException(
                $"'{fileName}' could not be opened as a PDF: {ex.Message}. If it is password-protected, remove the protection and try again.",
                ex);
        }

        using (document)
        {
            var sections = new List<DocumentSection>(document.NumberOfPages);
            var empty = 0;

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The nearest-neighbour word extractor keeps reading order in multi-column layouts, where
                // raw glyph order would interleave the columns into nonsense.
                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
                var text = Join(words.Select(w => w.Text));
                if (string.IsNullOrWhiteSpace(text))
                {
                    empty++;
                    continue;
                }

                sections.Add(new DocumentSection(text) { Page = page.Number });
            }

            if (sections.Count == 0)
            {
                throw new NetCoreAIException(
                    $"'{fileName}' has {document.NumberOfPages} page(s) but no text layer, so nothing could be extracted. It is probably a scan; run it through OCR first.");
            }

            if (empty > 0)
            {
                logger.LogInformation("{FileName}: {Empty} of {Total} pages had no text and were skipped (likely scanned images).", fileName, empty, document.NumberOfPages);
            }

            return Task.FromResult(new ExtractedDocument
            {
                Title = Title(document, fileName),
                Sections = sections,
                Metadata = Metadata(document),
            });
        }
    }

    /// <summary>
    /// Joins words with a single space. PdfPig reports words as the glyph runs it found, which can carry
    /// their own padding; left as-is that padding lands in every chunk and every quoted citation.
    /// </summary>
    private static string Join(IEnumerable<string> words)
    {
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            var trimmed = word.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }

    private static string Title(PdfDocument document, string fileName)
    {
        var title = document.Information.Title;
        return string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title.Trim();
    }

    private static Dictionary<string, string> Metadata(PdfDocument document)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pages"] = document.NumberOfPages.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        foreach (var (key, value) in new[]
        {
            ("author", document.Information.Author),
            ("subject", document.Information.Subject),
            ("keywords", document.Information.Keywords),
            ("producer", document.Information.Producer),
        })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value.Trim();
            }
        }

        return metadata;
    }
}
