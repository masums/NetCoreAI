using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Documents;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// PDF, Office and HTML extraction against files built here rather than committed fixtures, so the tests
/// say what the input contains and stay readable.
/// </summary>
public class DocumentExtractorTests
{
    private static async Task<ExtractedDocument> ExtractAsync(IDocumentExtractor extractor, byte[] bytes, string fileName, string? contentType = null)
    {
        using var stream = new MemoryStream(bytes);
        return await extractor.ExtractAsync(stream, fileName, contentType, TestContext.Current.CancellationToken);
    }

    // ---------- HTML ----------

    [Fact]
    public async Task Html_keeps_readable_text_and_drops_page_chrome()
    {
        const string html = """
            <html><head><title>Holiday policy</title><meta name="author" content="HR"></head>
            <body>
              <nav><a href="/">Home</a><a href="/about">About</a></nav>
              <script>trackEverything();</script>
              <style>.x{color:red}</style>
              <main>
                <h1>Holiday policy</h1>
                <p>Every employee gets 25 days of paid leave.</p>
                <h2>Carrying over</h2>
                <p>Up to five days may be carried into the next year.</p>
              </main>
              <footer>Copyright 2026</footer>
            </body></html>
            """;

        var document = await ExtractAsync(new HtmlExtractor(), Encoding.UTF8.GetBytes(html), "policy.html");
        var text = string.Join("\n", document.Sections.Select(s => s.Text));

        Assert.Equal("Holiday policy", document.Title);
        Assert.Equal("HR", document.Metadata["author"]);
        Assert.Contains("25 days of paid leave", text, StringComparison.Ordinal);

        // Navigation and footers repeat on every page of a crawl; indexing them makes every query match them.
        Assert.DoesNotContain("Home", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Copyright", text, StringComparison.Ordinal);
        Assert.DoesNotContain("trackEverything", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_headings_become_sections()
    {
        const string html = "<html><body><main><h1>One</h1><p>First body.</p><h2>Two</h2><p>Second body.</p></main></body></html>";

        var document = await ExtractAsync(new HtmlExtractor(), Encoding.UTF8.GetBytes(html), "doc.html");

        Assert.Equal(2, document.Sections.Count);
        Assert.Equal("One", document.Sections[0].Heading);
        Assert.Equal("Two", document.Sections[1].Heading);
        Assert.Equal(2, document.Sections[1].HeadingLevel);
    }

    [Fact]
    public async Task A_div_only_page_still_yields_its_text()
    {
        const string html = "<html><body><div><div>Some text with no semantic elements at all.</div></div></body></html>";

        var document = await ExtractAsync(new HtmlExtractor(), Encoding.UTF8.GetBytes(html), "spa.html");

        Assert.Contains("no semantic elements", Assert.Single(document.Sections).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_whitespace_is_collapsed()
    {
        Assert.Equal("one two three", HtmlExtractor.Normalize("  one\n\n   two\t\tthree  "));
    }

    // ---------- Word ----------

    [Fact]
    public async Task Docx_headings_become_sections_and_tables_keep_their_shape()
    {
        var bytes = BuildDocx();

        var document = await ExtractAsync(new DocxExtractor(), bytes, "handbook.docx");

        Assert.Equal(2, document.Sections.Count);
        Assert.Equal("Holiday", document.Sections[0].Heading);
        Assert.Contains("25 days", document.Sections[0].Text, StringComparison.Ordinal);

        var expenses = document.Sections[1];
        Assert.Equal("Expenses", expenses.Heading);

        // A table read as a run of cells is meaningless; rows must stay rows.
        Assert.Contains("Category | Limit", expenses.Text, StringComparison.Ordinal);
        Assert.Contains("Travel | 500", expenses.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_is_not_really_a_docx_is_refused_with_advice()
    {
        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await ExtractAsync(new DocxExtractor(), Encoding.UTF8.GetBytes("this is not a zip"), "old.docx"));

        // The commonest cause is a .doc renamed, so the message says what to do about it.
        Assert.Contains("could not be read as a Word document", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".doc", ex.Message, StringComparison.Ordinal);
    }

    // ---------- Excel ----------

    [Fact]
    public async Task Xlsx_rows_carry_their_column_headers_and_sheet_name()
    {
        var bytes = BuildXlsx();

        var document = await ExtractAsync(new XlsxExtractor(), bytes, "staff.xlsx");

        Assert.Equal(2, document.Sections.Count);
        Assert.Equal("name: Ada, role: Engineer", document.Sections[0].Text);
        Assert.Equal("People", document.Sections[0].Heading);
        Assert.Equal("People", document.Sections[0].Metadata!["sheet"]);
        Assert.Equal("2", document.Sections[0].Metadata!["row"]);
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("B7", 1)]
    [InlineData("Z1", 25)]
    [InlineData("AA1", 26)]
    [InlineData("AB3", 27)]
    public void Column_references_map_to_indexes(string reference, int expected)
    {
        // Excel omits empty cells, so the reference is the only way to keep columns aligned.
        Assert.Equal(expected, XlsxExtractor.ColumnIndex(reference));
    }

    // ---------- PDF ----------

    [Fact]
    public async Task Pdf_text_is_extracted_one_section_per_page_with_its_page_number()
    {
        var pdf = MinimalPdf.WithPages(
            "The holiday policy grants 25 days of paid leave.",
            "Expenses must be submitted within 30 days.",
            "Appendix A lists the exceptions.");

        var document = await ExtractAsync(new PdfExtractor(NullLogger<PdfExtractor>.Instance), pdf, "handbook.pdf");

        Assert.Equal(3, document.Sections.Count);
        Assert.Equal("3", document.Metadata["pages"]);

        // The page number is the whole point: a citation says "page 2", not "somewhere in the handbook".
        Assert.Equal([1, 2, 3], [.. document.Sections.Select(s => s.Page)]);
        Assert.Contains("25 days of paid leave", document.Sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("within 30 days", document.Sections[1].Text, StringComparison.Ordinal);
        Assert.Contains("Appendix A", document.Sections[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pdf_with_no_text_layer_says_it_needs_ocr()
    {
        var scanned = MinimalPdf.WithoutText(pages: 4);

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await ExtractAsync(new PdfExtractor(NullLogger<PdfExtractor>.Instance), scanned, "scan.pdf"));

        // "Empty document" would leave the user guessing; a scan needs a different action from a blank file.
        Assert.Contains("no text layer", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OCR", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4 page(s)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_extracted_pdf_flows_through_chunking_with_its_pages_intact()
    {
        var pdf = MinimalPdf.WithPages("Page one talks about holiday.", "Page two talks about expenses.");
        var document = await ExtractAsync(new PdfExtractor(NullLogger<PdfExtractor>.Instance), pdf, "handbook.pdf");

        var chunks = new NetCoreAI.Knowledge.RecursiveStructureChunker()
            .Chunk(document, new ChunkingOptions { MaxTokens = 200, MinTokens = 1 });

        // End to end: what the extractor knew about pages has to survive into the chunks that get embedded.
        Assert.Equal(2, chunks.Count);
        Assert.Equal(1, chunks[0].Page);
        Assert.Equal(2, chunks[1].Page);
    }

    // ---------- claims ----------

    [Theory]
    [InlineData("report.pdf", "application/pdf")]
    public void The_pdf_extractor_claims_pdfs(string fileName, string contentType)
    {
        var extractor = new PdfExtractor(NullLogger<PdfExtractor>.Instance);

        Assert.True(extractor.CanHandle(fileName, contentType));
        Assert.True(extractor.CanHandle(fileName, null));
        Assert.False(extractor.CanHandle("notes.txt", "text/plain"));
    }

    [Fact]
    public void Format_extractors_outrank_the_generic_ones()
    {
        // Priority is what stops a plain-text extractor claiming a PDF by content type.
        Assert.True(new PdfExtractor(NullLogger<PdfExtractor>.Instance).Priority > 0);
        Assert.True(new DocxExtractor().Priority > 0);
        Assert.True(new HtmlExtractor().Priority > 0);
    }

    [Fact]
    public async Task A_pdf_that_is_not_a_pdf_is_refused_with_a_reason()
    {
        var extractor = new PdfExtractor(NullLogger<PdfExtractor>.Instance);

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await ExtractAsync(extractor, Encoding.UTF8.GetBytes("definitely not a PDF"), "broken.pdf"));

        Assert.Contains("could not be opened as a PDF", ex.Message, StringComparison.Ordinal);
        Assert.Contains("password-protected", ex.Message, StringComparison.Ordinal);
    }

    // ---------- builders ----------

    /// <summary>A Word document with two headings, body text and a table.</summary>
    private static byte[] BuildDocx()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var body = document.AddMainDocumentPart().Document = new W.Document(new W.Body());
            var content = body.Body!;

            content.Append(Heading("Holiday", 1));
            content.Append(Paragraph("Every employee gets 25 days of paid leave."));
            content.Append(Heading("Expenses", 2));
            content.Append(Paragraph("Submit within 30 days."));
            content.Append(Table(("Category", "Limit"), ("Travel", "500")));
        }

        return stream.ToArray();
    }

    private static W.Paragraph Heading(string text, int level) =>
        new(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = $"Heading{level}" }),
            new W.Run(new W.Text(text)));

    private static W.Paragraph Paragraph(string text) => new(new W.Run(new W.Text(text)));

    private static W.Table Table(params (string First, string Second)[] rows)
    {
        var table = new W.Table();
        foreach (var (first, second) in rows)
        {
            table.Append(new W.TableRow(
                new W.TableCell(Paragraph(first)),
                new W.TableCell(Paragraph(second))));
        }

        return table;
    }

    /// <summary>A workbook with a header row and two data rows on a sheet called "People".</summary>
    private static byte[] BuildXlsx()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new X.Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var data = new X.SheetData();
            worksheetPart.Worksheet = new X.Worksheet(data);

            data.Append(Row(1, "name", "role"));
            data.Append(Row(2, "Ada", "Engineer"));
            data.Append(Row(3, "Grace", "Admiral"));

            workbookPart.Workbook.Append(new X.Sheets(new X.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "People",
            }));
        }

        return stream.ToArray();
    }

    private static X.Row Row(uint index, params string[] values)
    {
        var row = new X.Row { RowIndex = index };
        for (var i = 0; i < values.Length; i++)
        {
            row.Append(new X.Cell
            {
                CellReference = $"{(char)('A' + i)}{index}",
                DataType = X.CellValues.InlineString,
                InlineString = new X.InlineString(new X.Text(values[i])),
            });
        }

        return row;
    }
}
