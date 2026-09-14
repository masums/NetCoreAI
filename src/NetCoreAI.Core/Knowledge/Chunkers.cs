using System.Text;

namespace NetCoreAI.Knowledge;

/// <summary>
/// Shared chunking machinery. Token counts are approximated rather than tokenized: a chunker runs over
/// every document in a corpus, the target is a soft bound, and the embedding model truncates anyway.
/// Roughly four characters per token holds well enough for English prose and code.
/// </summary>
internal static class ChunkText
{
    private const double CharsPerToken = 4.0;

    public static int EstimateTokens(string text) => text.Length == 0 ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

    public static int TokensToChars(int tokens) => (int)(tokens * CharsPerToken);

    /// <summary>Splits into sentences on terminators, keeping the terminator with its sentence.</summary>
    public static List<string> Sentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '\n'))
            {
                continue;
            }

            // A terminator ends a sentence only when what follows looks like a new one, so "e.g." and
            // "3.5" do not split a sentence in half.
            var next = i + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }

            var isBreak = text[i] == '\n'
                || next >= text.Length
                || char.IsUpper(text[next])
                || char.IsDigit(text[next]) && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]);

            if (!isBreak)
            {
                continue;
            }

            var sentence = text[start..next].Trim();
            if (sentence.Length > 0)
            {
                sentences.Add(sentence);
            }

            start = next;
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
        {
            sentences.Add(tail);
        }

        return sentences;
    }

    /// <summary>The last <paramref name="overlapTokens"/> worth of text, cut on a word boundary.</summary>
    public static string Tail(string text, int overlapTokens)
    {
        if (overlapTokens <= 0 || text.Length == 0)
        {
            return string.Empty;
        }

        var chars = Math.Min(text.Length, TokensToChars(overlapTokens));
        var tail = text[^chars..];
        var space = tail.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? tail[(space + 1)..] : tail;
    }
}

/// <summary>
/// A fixed token window with overlap. Predictable and format-agnostic: the fallback when a document has
/// no structure worth respecting.
/// </summary>
internal sealed class FixedSizeChunker : IChunker
{
    public ChunkingStrategy Strategy => ChunkingStrategy.FixedSize;

    public IReadOnlyList<DocumentChunk> Chunk(ExtractedDocument document, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var chunks = new List<DocumentChunk>();
        var size = ChunkText.TokensToChars(Math.Max(1, options.MaxTokens));
        var overlap = ChunkText.TokensToChars(Math.Clamp(options.OverlapTokens, 0, options.MaxTokens - 1));

        foreach (var section in document.Sections)
        {
            var text = section.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var position = 0;
            while (position < text.Length)
            {
                var length = Math.Min(size, text.Length - position);
                var slice = text.Substring(position, length);
                ChunkBuilder.Add(chunks, slice, section, options);

                if (position + length >= text.Length)
                {
                    break;
                }

                position += Math.Max(1, size - overlap);
            }
        }

        return chunks;
    }
}

/// <summary>
/// Whole sentences packed up to the size limit. Nothing is cut mid-sentence, so a retrieved passage reads
/// as written — which matters when the passage is quoted back to a user as a citation.
/// </summary>
internal sealed class SentenceChunker : IChunker
{
    public ChunkingStrategy Strategy => ChunkingStrategy.Sentence;

    public IReadOnlyList<DocumentChunk> Chunk(ExtractedDocument document, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var chunks = new List<DocumentChunk>();
        foreach (var section in document.Sections)
        {
            PackSentences(chunks, ChunkText.Sentences(section.Text), section, options);
        }

        return chunks;
    }

    /// <summary>Fills chunks sentence by sentence, carrying an overlap tail into the next one.</summary>
    internal static void PackSentences(List<DocumentChunk> chunks, List<string> sentences, DocumentSection section, ChunkingOptions options)
    {
        var maxChars = ChunkText.TokensToChars(Math.Max(1, options.MaxTokens));
        var buffer = new StringBuilder();

        foreach (var sentence in sentences)
        {
            var separator = buffer.Length == 0 ? 0 : 1;
            if (buffer.Length > 0 && buffer.Length + separator + sentence.Length > maxChars)
            {
                // The buffer is as full as it goes: emit it, then start the next chunk with an overlap
                // tail so a fact spanning the boundary is still retrievable from either side.
                ChunkBuilder.Add(chunks, buffer.ToString(), section, options);
                var tail = ChunkText.Tail(buffer.ToString(), options.OverlapTokens);
                buffer.Clear();
                buffer.Append(tail);
            }

            if (buffer.Length > 0)
            {
                buffer.Append(' ');
            }

            buffer.Append(sentence);

            // One sentence longer than the whole window: emit it as it stands rather than cutting it.
            // An oversized chunk is better than a citation that stops mid-clause.
            if (buffer.Length >= maxChars)
            {
                ChunkBuilder.Add(chunks, buffer.ToString(), section, options);
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            ChunkBuilder.Add(chunks, buffer.ToString(), section, options);
        }
    }
}

/// <summary>
/// Splits on structure first: each heading starts a new chunk, paragraphs are kept whole where they fit,
/// and only an oversized paragraph falls back to sentences. Retrieval then returns passages that belong
/// together, and the citation can name the section they came from.
/// </summary>
internal sealed class RecursiveStructureChunker : IChunker
{
    public ChunkingStrategy Strategy => ChunkingStrategy.RecursiveStructure;

    public IReadOnlyList<DocumentChunk> Chunk(ExtractedDocument document, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var chunks = new List<DocumentChunk>();
        var maxChars = ChunkText.TokensToChars(options.MaxTokens);

        foreach (var section in document.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Text))
            {
                continue;
            }

            var buffer = new StringBuilder();
            foreach (var paragraph in Paragraphs(section.Text))
            {
                if (paragraph.Length > maxChars)
                {
                    // Too big to keep whole: flush what we have, then let sentences do the splitting.
                    Flush(chunks, buffer, section, options);
                    SentenceChunker.PackSentences(chunks, ChunkText.Sentences(paragraph), section, options);
                    continue;
                }

                if (buffer.Length + paragraph.Length + 2 > maxChars)
                {
                    Flush(chunks, buffer, section, options);
                }

                if (buffer.Length > 0)
                {
                    buffer.Append("\n\n");
                }

                buffer.Append(paragraph);
            }

            Flush(chunks, buffer, section, options);
        }

        return chunks;
    }

    private static void Flush(List<DocumentChunk> chunks, StringBuilder buffer, DocumentSection section, ChunkingOptions options)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        ChunkBuilder.Add(chunks, buffer.ToString(), section, options);
        buffer.Clear();
    }

    private static IEnumerable<string> Paragraphs(string text)
    {
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return paragraph;
        }
    }
}

/// <summary>
/// One chunk per section, for tabular sources where a row already is the unit of meaning and splitting it
/// would separate a value from its column.
/// </summary>
internal sealed class RowChunker : IChunker
{
    public ChunkingStrategy Strategy => ChunkingStrategy.Row;

    public IReadOnlyList<DocumentChunk> Chunk(ExtractedDocument document, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var chunks = new List<DocumentChunk>();
        foreach (var section in document.Sections)
        {
            // Rows are kept whole even when short: the minimum-size rule would drop legitimate rows.
            if (!string.IsNullOrWhiteSpace(section.Text))
            {
                ChunkBuilder.Add(chunks, section.Text, section, options, respectMinimum: false);
            }
        }

        return chunks;
    }
}

/// <summary>Builds chunk records so every chunker carries the same provenance and applies the same size rule.</summary>
internal static class ChunkBuilder
{
    public static void Add(List<DocumentChunk> chunks, string text, DocumentSection section, ChunkingOptions options, bool respectMinimum = true)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var tokens = ChunkText.EstimateTokens(trimmed);

        // A stray heading or page number retrieves noise and costs an embedding call.
        if (respectMinimum && tokens < options.MinTokens)
        {
            return;
        }

        chunks.Add(new DocumentChunk(trimmed, chunks.Count)
        {
            Page = section.Page,
            Section = section.Heading,
            TokenCount = tokens,
            Metadata = section.Metadata,
        });
    }
}
