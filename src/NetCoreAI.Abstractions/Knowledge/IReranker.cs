namespace NetCoreAI.Knowledge;

/// <summary>
/// Puts retrieved passages in the order a reader would put them.
/// </summary>
/// <remarks>
/// <para>
/// Retrieval and ranking are different jobs done well by different things. A vector search compares a
/// question and a passage that were embedded separately and never saw each other; a cross-encoder reads
/// the two together and answers one question — does this passage answer that question. It is far better at
/// it and far too slow to run over a corpus, which is why it goes second, over a few dozen candidates that
/// something cheap has already found.
/// </para>
/// <para>
/// The order is what decides the answer. A model given ten passages leans on the first two, so a correct
/// passage ranked seventh is, in practice, a passage that was not retrieved.
/// </para>
/// </remarks>
public interface IReranker
{
    /// <summary>What this reranker is, for logs and the dashboard.</summary>
    string Id { get; }

    /// <summary>
    /// Scores each passage against the question and returns the best <paramref name="topN"/>, best first.
    /// </summary>
    /// <remarks>
    /// Returns fewer than asked for when fewer were given, and never more. Implementations should leave
    /// the passages themselves untouched — a reranker reorders, it does not edit.
    /// </remarks>
    Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> chunks,
        int topN,
        CancellationToken cancellationToken = default);
}
