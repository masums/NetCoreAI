using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace NetCoreAI.Documents;

/// <summary>
/// Word documents. Headings become section boundaries, so a citation can name the section a passage came
/// from — Word's own outline is better structure than anything a chunker could infer from the text.
/// </summary>
public sealed class DocxExtractor : IDocumentExtractor
{
    public int Priority => 10;

    public bool CanHandle(string fileName, string? contentType) =>
        Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase)
        || contentType is "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = Seekable(content);
        using var document = Open(() => WordprocessingDocument.Open(buffer, false), fileName, "Word document");

        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new NetCoreAIException($"'{fileName}' has no document body.");

        var sections = new List<DocumentSection>();
        var buffer2 = new StringBuilder();
        string? heading = null;
        var level = 0;

        foreach (var element in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case W.Paragraph paragraph:
                    var text = paragraph.InnerText.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    if (HeadingLevel(paragraph) is { } headingLevel)
                    {
                        Flush(sections, buffer2, heading, level);
                        heading = text;
                        level = headingLevel;

                        // Keep the heading in the body: a chunk without it loses the line that names the topic.
                        buffer2.AppendLine(text);
                        continue;
                    }

                    buffer2.AppendLine(text);
                    break;

                case W.Table table:
                    // A table read as a run of cells is meaningless; rendering rows keeps the shape.
                    buffer2.AppendLine(RenderTable(table));
                    break;
            }
        }

        Flush(sections, buffer2, heading, level);

        return Task.FromResult(new ExtractedDocument
        {
            Title = document.PackageProperties.Title is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(fileName),
            Sections = sections,
            Metadata = Properties(document.PackageProperties),
        });
    }

    /// <summary>Word records outline level as a style name such as "Heading2".</summary>
    private static int? HeadingLevel(W.Paragraph paragraph)
    {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style is null || !style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(style["Heading".Length..], CultureInfo.InvariantCulture, out var level) ? level : 1;
    }

    private static string RenderTable(W.Table table)
    {
        var builder = new StringBuilder();
        foreach (var row in table.Elements<W.TableRow>())
        {
            var cells = row.Elements<W.TableCell>().Select(c => c.InnerText.Trim());
            builder.AppendLine(string.Join(" | ", cells));
        }

        return builder.ToString();
    }

    private static void Flush(List<DocumentSection> sections, StringBuilder buffer, string? heading, int level)
    {
        var text = buffer.ToString().Trim();
        buffer.Clear();
        if (text.Length > 0)
        {
            sections.Add(new DocumentSection(text) { Heading = heading, HeadingLevel = level });
        }
    }

    internal static MemoryStream Seekable(Stream content)
    {
        var buffer = new MemoryStream();
        content.CopyTo(buffer);
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>Opens an Open XML package, turning its failures into something a user can act on.</summary>
    internal static T Open<T>(Func<T> open, string fileName, string kind)
    {
        try
        {
            return open();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NetCoreAIException)
        {
            throw new NetCoreAIException(
                $"'{fileName}' could not be read as a {kind}: {ex.Message}. The older .doc/.xls/.ppt formats are not supported; re-save it in the current format.",
                ex);
        }
    }

    internal static Dictionary<string, string> Properties(IPackageProperties properties)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in new[]
        {
            ("author", properties.Creator),
            ("subject", properties.Subject),
            ("keywords", properties.Keywords),
            ("category", properties.Category),
        })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value.Trim();
            }
        }

        if (properties.Modified is { } modified)
        {
            metadata["modified"] = modified.ToString("O", CultureInfo.InvariantCulture);
        }

        return metadata;
    }
}

/// <summary>
/// PowerPoint. One section per slide, numbered as a page, so a citation can say which slide it came from.
/// Speaker notes are included: they often carry the explanation the slide only gestures at.
/// </summary>
public sealed class PptxExtractor : IDocumentExtractor
{
    public int Priority => 10;

    public bool CanHandle(string fileName, string? contentType) =>
        Path.GetExtension(fileName).Equals(".pptx", StringComparison.OrdinalIgnoreCase)
        || contentType is "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = DocxExtractor.Seekable(content);
        using var document = DocxExtractor.Open(() => PresentationDocument.Open(buffer, false), fileName, "PowerPoint presentation");

        var presentation = document.PresentationPart
            ?? throw new NetCoreAIException($"'{fileName}' has no presentation part.");

        var sections = new List<DocumentSection>();
        var slideNumber = 0;

        foreach (var slideId in presentation.Presentation?.SlideIdList?.ChildElements.OfType<P.SlideId>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            slideNumber++;

            if (slideId.RelationshipId?.Value is not { } relationshipId || presentation.GetPartById(relationshipId) is not SlidePart slide)
            {
                continue;
            }

            var builder = new StringBuilder();
            foreach (var text in slide.Slide?.Descendants<D.Text>() ?? [])
            {
                builder.AppendLine(text.Text);
            }

            var notes = slide.NotesSlidePart?.NotesSlide?.Descendants<D.Text>().Select(t => t.Text).ToList();
            if (notes is { Count: > 0 })
            {
                builder.AppendLine().AppendLine("Speaker notes: " + string.Join(' ', notes));
            }

            var slideText = builder.ToString().Trim();
            if (slideText.Length > 0)
            {
                sections.Add(new DocumentSection(slideText)
                {
                    Page = slideNumber,
                    Heading = Title(slide),
                });
            }
        }

        return Task.FromResult(new ExtractedDocument
        {
            Title = document.PackageProperties.Title is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(fileName),
            Sections = sections,
            Metadata = DocxExtractor.Properties(document.PackageProperties),
        });
    }

    /// <summary>The slide's title placeholder, which is the closest thing a deck has to a heading.</summary>
    private static string? Title(SlidePart slide)
    {
        foreach (var shape in slide.Slide?.Descendants<P.Shape>() ?? [])
        {
            var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
            if (placeholder?.Type?.Value is { } type && (type.Equals(P.PlaceholderValues.Title) || type.Equals(P.PlaceholderValues.CenteredTitle)))
            {
                var text = shape.InnerText.Trim();
                return text.Length > 0 ? text : null;
            }
        }

        return null;
    }
}

/// <summary>
/// Excel. One section per row, each carrying its column headers, because a row is the unit of meaning and
/// a bare tuple of values embeds into nothing useful.
/// </summary>
public sealed class XlsxExtractor : IDocumentExtractor
{
    public int Priority => 10;

    public bool CanHandle(string fileName, string? contentType) =>
        Path.GetExtension(fileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
        || contentType is "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = DocxExtractor.Seekable(content);
        using var document = DocxExtractor.Open(() => SpreadsheetDocument.Open(buffer, false), fileName, "Excel workbook");

        var workbook = document.WorkbookPart
            ?? throw new NetCoreAIException($"'{fileName}' has no workbook part.");

        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable;
        var sections = new List<DocumentSection>();

        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
            {
                continue;
            }

            var sheetName = sheet.Name?.Value ?? "Sheet";
            var rows = part.Worksheet?.Descendants<Row>().ToList() ?? [];
            if (rows.Count == 0)
            {
                continue;
            }

            // The first row is treated as headers, which is how spreadsheets are written in practice.
            var headers = Cells(rows[0], sharedStrings);
            for (var i = 1; i < rows.Count; i++)
            {
                var values = Cells(rows[i], sharedStrings);
                var builder = new StringBuilder();
                for (var column = 0; column < values.Count; column++)
                {
                    if (values[column].Length == 0)
                    {
                        continue;
                    }

                    var header = column < headers.Count && headers[column].Length > 0 ? headers[column] : $"column{column + 1}";
                    if (builder.Length > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(header).Append(": ").Append(values[column]);
                }

                if (builder.Length > 0)
                {
                    sections.Add(new DocumentSection(builder.ToString())
                    {
                        Heading = sheetName,
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["sheet"] = sheetName,
                            ["row"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                        },
                    });
                }
            }
        }

        return Task.FromResult(new ExtractedDocument
        {
            Title = document.PackageProperties.Title is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(fileName),
            Sections = sections,
            Metadata = DocxExtractor.Properties(document.PackageProperties),
        });
    }

    /// <summary>Cell values in column order, resolving shared strings and leaving gaps for empty cells.</summary>
    private static List<string> Cells(Row row, SharedStringTable? sharedStrings)
    {
        var values = new List<string>();
        var expected = 0;

        foreach (var cell in row.Elements<Cell>())
        {
            // Excel omits empty cells, so the column index has to come from the reference to keep alignment.
            var index = ColumnIndex(cell.CellReference?.Value);
            while (index > expected)
            {
                values.Add(string.Empty);
                expected++;
            }

            values.Add(Value(cell, sharedStrings));
            expected++;
        }

        return values;
    }

    private static string Value(Cell cell, SharedStringTable? sharedStrings)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings is not null
            && int.TryParse(raw, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < sharedStrings.ChildElements.Count)
        {
            return sharedStrings.ChildElements[index].InnerText.Trim();
        }

        return cell.DataType?.Value == CellValues.InlineString ? cell.InnerText.Trim() : raw.Trim();
    }

    /// <summary>Turns a cell reference such as "C7" into a 0-based column index.</summary>
    internal static int ColumnIndex(string? reference)
    {
        if (reference is not { Length: > 0 })
        {
            return 0;
        }

        var index = 0;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return Math.Max(0, index - 1);
    }
}
