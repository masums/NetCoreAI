using System.Globalization;
using System.Text;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Writes a small but genuinely valid PDF, so the PDF extractor can be tested against a real file with
/// real page boundaries rather than only on its error paths. Committing a binary fixture would hide what
/// the input actually contains; this way the test says it in full.
/// </summary>
internal static class MinimalPdf
{
    /// <summary>Builds a PDF with one page per string, each drawn as a single line of Helvetica.</summary>
    public static byte[] WithPages(params string[] pageTexts)
    {
        ArgumentNullException.ThrowIfNull(pageTexts);

        // Object numbering: 1 catalog, 2 page tree, 3 font, then a page and a content stream per page.
        var objects = new List<string>();
        var pageIds = new List<int>();
        var firstPageId = 4;

        for (var i = 0; i < pageTexts.Length; i++)
        {
            pageIds.Add(firstPageId + (i * 2));
        }

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id.ToString(CultureInfo.InvariantCulture)} 0 R"))}] /Count {pageTexts.Length.ToString(CultureInfo.InvariantCulture)} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (var i = 0; i < pageTexts.Length; i++)
        {
            var pageId = pageIds[i];
            var contentId = pageId + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentId.ToString(CultureInfo.InvariantCulture)} 0 R >>");

            var content = $"BT /F1 18 Tf 72 700 Td ({Escape(pageTexts[i])}) Tj ET";
            objects.Add($"<< /Length {content.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n{content}\nendstream");
        }

        return Assemble(objects);
    }

    /// <summary>A PDF with pages but no text at all: what a scan looks like before OCR.</summary>
    public static byte[] WithoutText(int pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{(3 + i).ToString(CultureInfo.InvariantCulture)} 0 R"))}] /Count {pages.ToString(CultureInfo.InvariantCulture)} >>",
        };

        for (var i = 0; i < pages; i++)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        }

        return Assemble(objects);
    }

    /// <summary>Writes the objects with a correct cross-reference table, which is what makes the file valid.</summary>
    private static byte[] Assemble(List<string> objects)
    {
        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");

        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefPosition = builder.Length;
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n");
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition.ToString(CultureInfo.InvariantCulture)}\n%%EOF");

        // Latin-1 keeps one byte per character, so the offsets computed above stay correct.
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
}
