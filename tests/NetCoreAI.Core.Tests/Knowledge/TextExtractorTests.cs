using System.Text;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// The extractors that need no third-party dependency. What matters is the structure they hand the
/// chunker: sections, headings and per-row metadata, since that is what ends up in a citation.
/// </summary>
public class TextExtractorTests
{
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<ExtractedDocument> ExtractAsync(IDocumentExtractor extractor, string text, string fileName)
    {
        using var stream = Stream(text);
        return await extractor.ExtractAsync(stream, fileName, null, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("readme.md")]
    [InlineData("app.log")]
    public void Plain_text_claims_the_formats_it_handles(string fileName)
    {
        Assert.True(new PlainTextExtractor().CanHandle(fileName, null));
    }

    [Fact]
    public void Plain_text_does_not_claim_a_pdf_or_a_spreadsheet()
    {
        var extractor = new PlainTextExtractor();

        // Claiming these would shadow the real extractors, which is worse than not handling them.
        Assert.False(extractor.CanHandle("report.pdf", "application/pdf"));
        Assert.False(extractor.CanHandle("data.xlsx", null));
    }

    [Fact]
    public async Task Plain_text_is_one_section()
    {
        var document = await ExtractAsync(new PlainTextExtractor(), "Just some notes.\nOn two lines.", "notes.txt");

        Assert.Equal("notes", document.Title);
        Assert.Equal("Just some notes.\nOn two lines.", Assert.Single(document.Sections).Text);
    }

    [Fact]
    public async Task Markdown_headings_become_sections_that_keep_their_heading()
    {
        var markdown = """
            # Employee handbook

            Welcome to the company.

            ## Holiday

            You get 25 days.

            ## Expenses

            Submit within 30 days.
            """;

        var document = await ExtractAsync(new PlainTextExtractor(), markdown, "handbook.md");

        Assert.Equal("Employee handbook", document.Title);
        Assert.Equal(3, document.Sections.Count);

        var holiday = document.Sections.Single(s => s.Heading == "Holiday");
        Assert.Equal(2, holiday.HeadingLevel);

        // The heading stays in the body: a chunk without it loses the line naming the topic.
        Assert.Contains("Holiday", holiday.Text, StringComparison.Ordinal);
        Assert.Contains("25 days", holiday.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hash_that_is_not_a_heading_does_not_split_the_document()
    {
        // "#hashtag" and a shell comment are not ATX headings; splitting on them would shred the text.
        var document = await ExtractAsync(new PlainTextExtractor(), "# Real heading\n\nSee #hashtag and #!/bin/sh here.", "notes.md");

        Assert.Single(document.Sections);
    }

    [Fact]
    public async Task Csv_rows_become_sections_that_carry_their_column_names()
    {
        var document = await ExtractAsync(new CsvExtractor(), "name,role,city\nAda,Engineer,London\nGrace,Admiral,Arlington", "staff.csv");

        Assert.Equal(2, document.Sections.Count);

        // "Ada" alone embeds into nothing; "name: Ada, role: Engineer" is retrievable.
        Assert.Equal("name: Ada, role: Engineer, city: London", document.Sections[0].Text);
        Assert.Equal("1", document.Sections[0].Metadata!["row"]);
        Assert.Equal("name, role, city", document.Metadata["columns"]);
    }

    [Fact]
    public void Csv_parsing_handles_quotes_separators_and_newlines_inside_fields()
    {
        var rows = CsvExtractor.ParseRows("name,note\n\"Ada, Countess\",\"She said \"\"hello\"\"\nacross two lines\"\nGrace,plain", ',');

        Assert.Equal(3, rows.Count);
        Assert.Equal("Ada, Countess", rows[1][0]);
        Assert.Contains("said \"hello\"", rows[1][1], StringComparison.Ordinal);
        Assert.Contains("across two lines", rows[1][1], StringComparison.Ordinal);
        Assert.Equal("Grace", rows[2][0]);
    }

    [Fact]
    public async Task Empty_csv_cells_are_left_out_rather_than_embedded_as_blanks()
    {
        var document = await ExtractAsync(new CsvExtractor(), "name,role,city\nAda,,London", "staff.csv");

        Assert.Equal("name: Ada, city: London", Assert.Single(document.Sections).Text);
    }

    [Fact]
    public async Task A_tsv_is_split_on_tabs()
    {
        var document = await ExtractAsync(new CsvExtractor(), "name\trole\nAda\tEngineer", "staff.tsv");

        Assert.Equal("name: Ada, role: Engineer", Assert.Single(document.Sections).Text);
    }

    [Fact]
    public async Task A_json_array_becomes_one_section_per_element()
    {
        var json = """[{"name":"Ada","role":"Engineer"},{"name":"Grace","role":"Admiral"}]""";
        var document = await ExtractAsync(new JsonExtractor(), json, "staff.json");

        Assert.Equal(2, document.Sections.Count);
        Assert.Contains("name: Ada", document.Sections[0].Text, StringComparison.Ordinal);
        Assert.Equal("0", document.Sections[0].Metadata!["index"]);
    }

    [Fact]
    public async Task Nested_json_is_flattened_to_paths_so_field_names_reach_the_embedding()
    {
        var json = """{"employee":{"name":"Ada","skills":["maths","engines"]},"active":true}""";
        var document = await ExtractAsync(new JsonExtractor(), json, "record.json");

        var text = Assert.Single(document.Sections).Text;
        Assert.Contains("employee.name: Ada", text, StringComparison.Ordinal);
        Assert.Contains("employee.skills[0]: maths", text, StringComparison.Ordinal);
        Assert.Contains("active: True", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_lines_are_one_section_each_and_a_bad_line_is_skipped()
    {
        var jsonl = """
            {"id":1,"text":"first"}
            not json at all
            {"id":2,"text":"second"}
            """;

        var document = await ExtractAsync(new JsonExtractor(), jsonl, "export.jsonl");

        // One malformed line in a large export must not fail the whole file.
        Assert.Equal(2, document.Sections.Count);
        Assert.Contains("first", document.Sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("second", document.Sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_says_what_is_wrong_with_it()
    {
        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await ExtractAsync(new JsonExtractor(), "{ not json", "broken.json"));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
        Assert.Contains("broken.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_json_values_are_dropped_rather_than_embedded_as_the_word_null()
    {
        var document = await ExtractAsync(new JsonExtractor(), """{"name":"Ada","manager":null}""", "record.json");

        var text = Assert.Single(document.Sections).Text;
        Assert.Contains("name: Ada", text, StringComparison.Ordinal);
        Assert.DoesNotContain("manager", text, StringComparison.Ordinal);
    }
}
