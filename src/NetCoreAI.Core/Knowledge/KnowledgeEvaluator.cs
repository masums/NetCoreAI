using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Knowledge;

/// <summary>Measuring whether a knowledge base actually answers the questions it is asked.</summary>
public interface IKnowledgeEvaluator
{
    Task<IReadOnlyList<EvaluationSet>> ListSetsAsync(string? knowledgeBaseId = null, CancellationToken cancellationToken = default);

    Task<EvaluationSet> SaveSetAsync(EvaluationSet set, CancellationToken cancellationToken = default);

    Task DeleteSetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Runs a set and records the result, so two configurations can be compared afterwards.</summary>
    Task<EvaluationRun> RunAsync(string setId, EvaluationOptions? options = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvaluationRun>> ListRunsAsync(string setId, int limit = 50, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs a set of questions against a knowledge base and reports what it found.
/// </summary>
/// <remarks>
/// <para>
/// The point is comparison rather than an absolute number. Run the same set with vectors alone and again
/// with hybrid search and a reranker, and the difference is evidence; the hit rate on its own is a number
/// whose meaning depends entirely on how the questions were written.
/// </para>
/// <para>
/// Which is worth saying plainly: a set written by reading the documents and inventing questions about
/// them measures whether retrieval can find a passage the author was looking at. That is a much easier
/// task than the one real users set, and a set built that way will flatter every configuration equally.
/// </para>
/// </remarks>
internal sealed class KnowledgeEvaluator(
    IMetadataStore store,
    IRetriever retriever,
    IChatClientFactory clients,
    ILogger<KnowledgeEvaluator> logger) : IKnowledgeEvaluator
{
    public Task<IReadOnlyList<EvaluationSet>> ListSetsAsync(string? knowledgeBaseId = null, CancellationToken cancellationToken = default) =>
        store.Evaluations.ListSetsAsync(knowledgeBaseId, cancellationToken);

    public async Task<EvaluationSet> SaveSetAsync(EvaluationSet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);

        if (await store.Knowledge.GetAsync(set.KnowledgeBaseId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new NetCoreAIException($"No knowledge base with id '{set.KnowledgeBaseId}'.");
        }

        var saved = set with { UpdatedAt = DateTimeOffset.UtcNow };
        await store.Evaluations.UpsertSetAsync(saved, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public Task DeleteSetAsync(string id, CancellationToken cancellationToken = default) =>
        store.Evaluations.DeleteSetAsync(id, cancellationToken);

    public Task<IReadOnlyList<EvaluationRun>> ListRunsAsync(string setId, int limit = 50, CancellationToken cancellationToken = default) =>
        store.Evaluations.ListRunsAsync(setId, limit, cancellationToken);

    public async Task<EvaluationRun> RunAsync(string setId, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var set = await store.Evaluations.GetSetAsync(setId, cancellationToken).ConfigureAwait(false)
            ?? throw new NetCoreAIException($"No evaluation set with id '{setId}'.");

        if (set.Cases.Count == 0)
        {
            throw new NetCoreAIException($"Evaluation set '{set.Name}' has no questions in it.");
        }

        options ??= new EvaluationOptions();
        var started = Stopwatch.GetTimestamp();
        var results = new List<EvaluationCaseResult>(set.Cases.Count);

        foreach (var testCase in set.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunCaseAsync(set, testCase, options, cancellationToken).ConfigureAwait(false));
        }

        var judged = results.Where(r => r.Faithfulness is not null).ToList();
        var run = new EvaluationRun
        {
            Id = Guid.NewGuid().ToString("N"),
            SetId = set.Id,
            KnowledgeBaseId = set.KnowledgeBaseId,
            Retrieval = options.Retrieval,
            Label = options.Label,
            Cases = results,
            HitRate = results.Count == 0 ? 0 : (float)results.Count(r => r.Hit) / results.Count,

            // Cases that found nothing contribute zero rather than being left out, or a configuration that
            // retrieves one document perfectly and nothing else would score a perfect MRR.
            MeanReciprocalRank = results.Count == 0
                ? 0
                : results.Sum(r => r.FirstHitRank is { } rank ? 1f / rank : 0f) / results.Count,
            MeanFaithfulness = judged.Count == 0 ? null : judged.Average(r => r.Faithfulness!.Value),
            JudgeModel = judged.Count == 0 ? null : options.JudgeModel,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };

        await store.Evaluations.AddRunAsync(run, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Evaluated {Set} against {KnowledgeBase}: {Hit:P0} hit rate, {Mrr:F2} MRR over {Count} question(s){Label}.",
            set.Name, set.KnowledgeBaseId, run.HitRate, run.MeanReciprocalRank, results.Count,
            options.Label is { Length: > 0 } label ? $" ({label})" : "");

        return run;
    }

    private async Task<EvaluationCaseResult> RunCaseAsync(
        EvaluationSet set,
        EvaluationCase testCase,
        EvaluationOptions options,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        // No caller tags: an evaluation measures what the base can find, not what one person may see. A
        // run filtered by somebody's access would report a worse number for a reason that is not about
        // retrieval at all.
        var hits = await retriever.SearchAsync(set.KnowledgeBaseId, testCase.Question, options.Retrieval, null, cancellationToken)
            .ConfigureAwait(false);

        var retrieved = hits.Select(h => h.Chunk.DocumentId).ToList();
        var expected = testCase.ExpectedDocumentIds;

        int? firstHit = null;
        for (var i = 0; i < retrieved.Count; i++)
        {
            if (expected.Contains(retrieved[i], StringComparer.OrdinalIgnoreCase))
            {
                firstHit = i + 1;
                break;
            }
        }

        string? answer = null;
        float? faithfulness = null;
        string? reason = null;

        if (options.GenerateAnswers && hits.Count > 0)
        {
            answer = await AnswerAsync(set, testCase, hits, cancellationToken).ConfigureAwait(false);

            if (options.JudgeModel is { Length: > 0 } judge && answer is { Length: > 0 })
            {
                (faithfulness, reason) = await JudgeAsync(judge, testCase.Question, answer, hits, cancellationToken).ConfigureAwait(false);
            }
        }

        return new EvaluationCaseResult
        {
            Question = testCase.Question,
            RetrievedDocumentIds = retrieved,

            // Expecting nothing in particular means retrieval cannot be wrong, so the case counts as a hit
            // when anything came back. Such a case measures that the base is not empty, and nothing more.
            Hit = expected.Count == 0 ? retrieved.Count > 0 : firstHit is not null,
            FirstHitRank = firstHit,
            Answer = answer,
            Faithfulness = faithfulness,
            FaithfulnessReason = reason,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }

    private async Task<string?> AnswerAsync(
        EvaluationSet set,
        EvaluationCase testCase,
        IReadOnlyList<RetrievedChunk> hits,
        CancellationToken cancellationToken)
    {
        try
        {
            // The default chat model. A knowledge base names an embedding model, not a chat one, and the
            // answer here exists to be judged rather than to be served to anybody.
            var client = clients.Get(ModelAlias.Default);

            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, "Answer only from the passages. If they do not contain the answer, say so."),
                    new ChatMessage(ChatRole.User, $"{Passages(hits)}\n\nQuestion: {testCase.Question}"),
                ],
                new ChatOptions { Temperature = 0 },
                cancellationToken).ConfigureAwait(false);

            return response.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One question that could not be answered should not lose the retrieval numbers for the rest
            // of the set, which are the part that does not need a model.
            logger.LogWarning(ex, "Could not generate an answer while evaluating {Set}.", set.Name);
            return null;
        }
    }

    /// <summary>
    /// Asks a model how much of an answer its passages actually support.
    /// </summary>
    /// <remarks>
    /// A model grading a model. Worth having for noticing that something got worse between two runs; not
    /// ground truth, and the reason is kept because a score nobody can argue with is a score nobody should
    /// act on.
    /// </remarks>
    private async Task<(float? Score, string? Reason)> JudgeAsync(
        string judgeModel,
        string question,
        string answer,
        IReadOnlyList<RetrievedChunk> hits,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await clients.Get(judgeModel).GetResponseAsync(
                [
                    new ChatMessage(
                        ChatRole.System,
                        "You are grading whether an answer is supported by the passages it was given. "
                        + "Reply with a score from 0 to 1 and one sentence of reason, as: SCORE|REASON. "
                        + "1 means every claim is supported by the passages. 0 means none is. "
                        + "Judge only support by the passages, not whether the answer is true in general."),
                    new ChatMessage(
                        ChatRole.User,
                        $"{Passages(hits)}\n\nQuestion: {question}\n\nAnswer: {answer}"),
                ],
                new ChatOptions { Temperature = 0, MaxOutputTokens = 200 },
                cancellationToken).ConfigureAwait(false);

            return Parse(response.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The judge model ({Model}) could not score an answer.", judgeModel);
            return (null, null);
        }
    }

    /// <summary>
    /// Reads "0.8|the second claim is not in the passages" out of whatever the judge said.
    /// </summary>
    /// <remarks>
    /// Returns null rather than a guess when there is no number to read. A judge that rambled should leave
    /// a gap in the average, not a zero — scoring an unparseable reply as "completely unsupported" would
    /// make a chatty model look like a retrieval problem.
    /// </remarks>
    internal static (float? Score, string? Reason) Parse(string? reply)
    {
        if (reply is not { Length: > 0 })
        {
            return (null, null);
        }

        var parts = reply.Split('|', 2);
        var head = parts[0].Trim();

        var number = new string([.. head.TakeWhile(c => char.IsAsciiDigit(c) || c is '.' or ',')]).Replace(',', '.');
        if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
        {
            return (null, reply.Trim());
        }

        return (Math.Clamp(score, 0f, 1f), parts.Length > 1 ? parts[1].Trim() : null);
    }

    private static string Passages(IReadOnlyList<RetrievedChunk> hits)
    {
        var builder = new StringBuilder("Passages:\n");
        foreach (var hit in hits)
        {
            builder.Append("- ").AppendLine(hit.Chunk.Text);
        }

        return builder.ToString();
    }
}
