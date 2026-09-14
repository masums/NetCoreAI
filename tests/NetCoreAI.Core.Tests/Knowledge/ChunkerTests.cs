using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// Chunking decides what retrieval can return, so these tests are about the boundaries: nothing lost,
/// nothing cut where it would change meaning, and provenance carried through to the citation.
/// </summary>
public class ChunkerTests
{
    private static ExtractedDocument Document(params DocumentSection[] sections) =>
        new() { Title = "Test", Sections = sections };

    private static DocumentSection Section(string text, int? page = null, string? heading = null) =>
        new(text) { Page = page, Heading = heading };

    /// <summary>Prose long enough to force several chunks at the sizes used here.</summary>
    private static string Prose(int sentences) =>
        string.Join(' ', Enumerable.Range(1, sentences).Select(i => $"This is sentence number {i} and it carries enough words to take up room."));

    [Fact]
    public void Fixed_size_covers_the_whole_text_with_overlap()
    {
        var text = Prose(40);
        var chunks = new FixedSizeChunker().Chunk(Document(Section(text)), new ChunkingOptions { MaxTokens = 50, OverlapTokens = 10, MinTokens = 1 });

        Assert.True(chunks.Count > 1, "long text must split");

        // Overlap means the pieces sum to more than the original, but nothing may be missing: the first
        // and last characters of the document have to appear somewhere.
        Assert.StartsWith(text[..20], chunks[0].Text, StringComparison.Ordinal);
        Assert.EndsWith(text[^20..], chunks[^1].Text, StringComparison.Ordinal);
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    [Fact]
    public void Fixed_size_chunks_are_indexed_in_order()
    {
        var chunks = new FixedSizeChunker().Chunk(Document(Section(Prose(30))), new ChunkingOptions { MaxTokens = 40, OverlapTokens = 5, MinTokens = 1 });

        Assert.Equal([.. Enumerable.Range(0, chunks.Count)], [.. chunks.Select(c => c.Index)]);
    }

    [Fact]
    public void Sentence_chunking_never_cuts_a_sentence_in_half()
    {
        var chunks = new SentenceChunker().Chunk(Document(Section(Prose(20))), new ChunkingOptions { MaxTokens = 40, OverlapTokens = 0, MinTokens = 1 });

        Assert.True(chunks.Count > 1);

        // Every chunk ends where a sentence ends: a citation quoted to a user should not trail off.
        Assert.All(chunks, c => Assert.EndsWith(".", c.Text.TrimEnd(), StringComparison.Ordinal));
    }

    [Fact]
    public void A_sentence_longer_than_the_window_survives_whole()
    {
        var monster = "This one sentence runs on and on " + string.Join(' ', Enumerable.Repeat("and on", 200)) + ".";
        var chunks = new SentenceChunker().Chunk(Document(Section(monster)), new ChunkingOptions { MaxTokens = 20, OverlapTokens = 0, MinTokens = 1 });

        // Better one oversized chunk than a quote that stops mid-clause.
        var chunk = Assert.Single(chunks);
        Assert.Equal(monster, chunk.Text);
    }

    [Fact]
    public void Sentence_splitting_is_not_fooled_by_abbreviations_and_decimals()
    {
        var text = "The model scores 3.5 on the benchmark. It was trained on data from e.g. books and code. That is all.";
        var chunks = new SentenceChunker().Chunk(Document(Section(text)), new ChunkingOptions { MaxTokens = 10, OverlapTokens = 0, MinTokens = 1 });

        // "3.5" and "e.g." must not be read as sentence ends, or the chunks would be nonsense fragments.
        Assert.All(chunks, c => Assert.DoesNotContain("3.\n", c.Text, StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Text.Contains("3.5 on the benchmark", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Text.Contains("e.g. books and code", StringComparison.Ordinal));
    }

    [Fact]
    public void Structure_chunking_keeps_paragraphs_whole_when_they_fit()
    {
        var document = Document(Section("First paragraph, short and self-contained.\n\nSecond paragraph, also short."));
        var chunks = new RecursiveStructureChunker().Chunk(document, new ChunkingOptions { MaxTokens = 500, OverlapTokens = 0, MinTokens = 1 });

        // Both fit in one window, so they stay together rather than being split arbitrarily.
        var chunk = Assert.Single(chunks);
        Assert.Contains("First paragraph", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Second paragraph", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Structure_chunking_falls_back_to_sentences_for_an_oversized_paragraph()
    {
        var chunks = new RecursiveStructureChunker().Chunk(
            Document(Section(Prose(30))),
            new ChunkingOptions { MaxTokens = 40, OverlapTokens = 0, MinTokens = 1 });

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.EndsWith(".", c.Text.TrimEnd(), StringComparison.Ordinal));
    }

    [Fact]
    public void Page_and_heading_travel_with_the_chunk()
    {
        var document = Document(
            Section(Prose(3), page: 1, heading: "Introduction"),
            Section(Prose(3), page: 7, heading: "Results"));

        var chunks = new RecursiveStructureChunker().Chunk(document, new ChunkingOptions { MaxTokens = 500, MinTokens = 1 });

        // Without this a citation cannot say which page it came from, which is the exit criterion for Phase 2.
        Assert.Contains(chunks, c => c.Page == 1 && c.Section == "Introduction");
        Assert.Contains(chunks, c => c.Page == 7 && c.Section == "Results");
    }

    [Fact]
    public void Rows_are_one_chunk_each_even_when_short()
    {
        var document = Document(
            new DocumentSection("id: 1, name: Ada") { Metadata = new Dictionary<string, string> { ["row"] = "1" } },
            new DocumentSection("id: 2, name: Grace") { Metadata = new Dictionary<string, string> { ["row"] = "2" } });

        var chunks = new RowChunker().Chunk(document, new ChunkingOptions { MinTokens = 50 });

        // A row is the unit of meaning; the minimum-size rule would throw away perfectly good rows.
        Assert.Equal(2, chunks.Count);
        Assert.Equal("1", chunks[0].Metadata!["row"]);
    }

    [Fact]
    public void Fragments_below_the_minimum_are_dropped()
    {
        var chunks = new RecursiveStructureChunker().Chunk(
            Document(Section("1"), Section("Table of contents"), Section(Prose(10))),
            new ChunkingOptions { MaxTokens = 200, MinTokens = 20 });

        // A page number or a stray heading retrieves noise and costs an embedding call.
        Assert.DoesNotContain(chunks, c => c.Text == "1");
        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void An_empty_document_produces_nothing_rather_than_an_empty_chunk()
    {
        var options = new ChunkingOptions { MinTokens = 1 };

        Assert.Empty(new RecursiveStructureChunker().Chunk(Document(Section("   ")), options));
        Assert.Empty(new FixedSizeChunker().Chunk(Document(Section("")), options));
        Assert.Empty(new SentenceChunker().Chunk(Document(), options));
    }

    [Theory]
    [InlineData(ChunkingStrategy.FixedSize)]
    [InlineData(ChunkingStrategy.Sentence)]
    [InlineData(ChunkingStrategy.RecursiveStructure)]
    [InlineData(ChunkingStrategy.Row)]
    public void Every_strategy_has_exactly_one_chunker(ChunkingStrategy strategy)
    {
        IChunker[] chunkers = [new FixedSizeChunker(), new SentenceChunker(), new RecursiveStructureChunker(), new RowChunker()];

        Assert.Single(chunkers, c => c.Strategy == strategy);
    }
}
