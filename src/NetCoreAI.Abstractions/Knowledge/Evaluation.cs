namespace NetCoreAI.Knowledge;

/// <summary>One question, and what a good answer to it would have used.</summary>
public sealed record EvaluationCase
{
    public required string Question { get; init; }

    /// <summary>
    /// Documents that ought to be retrieved for this question.
    /// </summary>
    /// <remarks>
    /// The whole measurement rests on these being right. A set written by reading the documents and
    /// inventing questions about them measures whether retrieval can find a passage somebody was looking
    /// at while they wrote the question — which is a much easier task than the one users set. Questions
    /// taken from what people actually asked are worth more than a hundred written to order.
    /// </remarks>
    public IReadOnlyList<string> ExpectedDocumentIds { get; init; } = [];

    /// <summary>What a correct answer says, when there is one worth writing down.</summary>
    public string? ExpectedAnswer { get; init; }

    public string? Notes { get; init; }
}

/// <summary>A named set of questions to measure a knowledge base against.</summary>
public sealed record EvaluationSet
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The knowledge base these questions are about.</summary>
    public required string KnowledgeBaseId { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<EvaluationCase> Cases { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>What happened for one question.</summary>
public sealed record EvaluationCaseResult
{
    public required string Question { get; init; }

    /// <summary>Documents retrieval actually returned, in order.</summary>
    public IReadOnlyList<string> RetrievedDocumentIds { get; init; } = [];

    /// <summary>True when at least one expected document was retrieved at all.</summary>
    public bool Hit { get; init; }

    /// <summary>
    /// 1-based position of the first expected document, or null when none was found.
    /// </summary>
    /// <remarks>
    /// The number re-ranking moves. A model given ten passages leans on the first two, so finding the
    /// right one in position seven is barely better than not finding it.
    /// </remarks>
    public int? FirstHitRank { get; init; }

    /// <summary>The answer generated, when the run asked for one.</summary>
    public string? Answer { get; init; }

    /// <summary>0–1 from the judge model: how much of the answer the passages actually support.</summary>
    public float? Faithfulness { get; init; }

    /// <summary>The judge's reason, kept because a score without one cannot be argued with.</summary>
    public string? FaithfulnessReason { get; init; }

    public long ElapsedMs { get; init; }
}

/// <summary>What a whole set scored.</summary>
public sealed record EvaluationRun
{
    public required string Id { get; init; }

    public required string SetId { get; init; }

    public required string KnowledgeBaseId { get; init; }

    /// <summary>The retrieval settings this run used, so two runs can be told apart afterwards.</summary>
    public RetrievalOptions? Retrieval { get; init; }

    /// <summary>What the run was for: "hybrid + reranker", "before the re-index".</summary>
    public string? Label { get; init; }

    public IReadOnlyList<EvaluationCaseResult> Cases { get; init; } = [];

    /// <summary>Fraction of questions where an expected document was retrieved at all.</summary>
    public float HitRate { get; init; }

    /// <summary>
    /// Mean reciprocal rank: the average of 1/(position of the first correct document).
    /// </summary>
    /// <remarks>
    /// Reported beside the hit rate because the two move independently and only one of them is about
    /// answer quality. Retrieval that finds the right document every time, in position eight every time,
    /// has a perfect hit rate and produces bad answers.
    /// </remarks>
    public float MeanReciprocalRank { get; init; }

    /// <summary>Average faithfulness across the cases a judge scored, or null when none were.</summary>
    public float? MeanFaithfulness { get; init; }

    /// <summary>The model that did the judging, named because a score is only as good as its judge.</summary>
    public string? JudgeModel { get; init; }

    public DateTimeOffset RanAt { get; init; } = DateTimeOffset.UtcNow;

    public long ElapsedMs { get; init; }
}

/// <summary>How to run an evaluation.</summary>
public sealed record EvaluationOptions
{
    /// <summary>
    /// Retrieval settings to use instead of the base's own.
    /// </summary>
    /// <remarks>
    /// The point of the whole feature: run one set twice, once with vectors alone and once with hybrid and
    /// a reranker, and read the difference instead of assuming it.
    /// </remarks>
    public RetrievalOptions? Retrieval { get; init; }

    /// <summary>Generate an answer per question, so faithfulness can be judged. Slower, and the only way to score it.</summary>
    public bool GenerateAnswers { get; init; }

    /// <summary>
    /// Model that scores whether an answer is supported by its passages. Null skips the judging.
    /// </summary>
    /// <remarks>
    /// A model grading a model. Useful for noticing that something got worse; not ground truth, and never
    /// worth arguing with a person about.
    /// </remarks>
    public string? JudgeModel { get; init; }

    public string? Label { get; init; }
}
