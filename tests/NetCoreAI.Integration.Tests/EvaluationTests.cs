using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Measuring a knowledge base rather than assuming it works.
/// </summary>
/// <remarks>
/// The point is comparison: run one set of questions with vectors alone and again with hybrid search and a
/// reranker, and read the difference. A hit rate on its own is a number whose meaning depends entirely on
/// how the questions were written.
/// </remarks>
public sealed class EvaluationTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    private IKnowledgeEvaluator Evaluator => _app.Services.GetRequiredService<IKnowledgeEvaluator>();

    public async ValueTask InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        // Query embeddings without a model: what is measured here is which documents come back, not
        // whether the embedding is any good.
        builder.Services.AddSingleton<IChatClientFactory, FixedEmbeddings>();

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();

        await IndexAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Three documents. "refunds" is closest to the query vector, "shipping" furthest.
    /// </summary>
    private async Task IndexAsync()
    {
        var knowledge = _app.Services.GetRequiredService<IKnowledgeService>();
        await knowledge.CreateAsync(new KnowledgeBase { Id = "docs", Name = "Docs", EmbeddingModel = "fake" }, Ct);

        var collection = (await knowledge.GetAsync("docs", Ct))!.Collection;
        var store = _app.Services.GetRequiredService<IEnumerable<IVectorStore>>().First();

        await store.EnsureCollectionAsync(collection, 2, VectorDistance.Cosine, Ct);
        await store.UpsertAsync(
            collection,
            [
                new VectorRecord("c1", "refunds", new float[] { 1f, 0f }, "how refunds work", new Dictionary<string, string>(), []),
                new VectorRecord("c2", "returns", new float[] { 0.9f, 0.1f }, "how returns work", new Dictionary<string, string>(), []),
                new VectorRecord("c3", "shipping", new float[] { 0.1f, 1f }, "how shipping works", new Dictionary<string, string>(), []),
            ],
            Ct);
    }

    private Task<EvaluationSet> SaveSetAsync(params EvaluationCase[] cases) =>
        Evaluator.SaveSetAsync(
            new EvaluationSet { Id = "set1", Name = "Support questions", KnowledgeBaseId = "docs", Cases = cases },
            Ct);

    // ---------- the measurements ----------

    [Fact]
    public async Task A_question_whose_document_is_retrieved_counts_as_a_hit()
    {
        await SaveSetAsync(new EvaluationCase { Question = "refunds", ExpectedDocumentIds = ["refunds"] });

        var run = await Evaluator.RunAsync("set1", cancellationToken: Ct);

        Assert.Equal(1f, run.HitRate);
        Assert.Equal(1, Assert.Single(run.Cases).FirstHitRank);
    }

    [Fact]
    public async Task A_question_whose_document_is_never_retrieved_does_not()
    {
        await SaveSetAsync(new EvaluationCase { Question = "anything", ExpectedDocumentIds = ["not-indexed"] });

        var run = await Evaluator.RunAsync("set1", cancellationToken: Ct);

        Assert.Equal(0f, run.HitRate);
        Assert.Null(Assert.Single(run.Cases).FirstHitRank);
    }

    [Fact]
    public async Task The_rank_of_the_first_correct_document_is_reported_not_just_whether_it_was_found()
    {
        await SaveSetAsync(new EvaluationCase { Question = "anything", ExpectedDocumentIds = ["shipping"] });

        // MinScore off, so the point being tested is the rank rather than the similarity threshold.
        var run = await Evaluator.RunAsync("set1", new EvaluationOptions { Retrieval = new RetrievalOptions { TopK = 3, MinScore = null } }, Ct);

        // Retrieval that finds the right document every time, in position three every time, has a perfect
        // hit rate and produces bad answers — a model leans on the first passages it is given.
        Assert.Equal(1f, run.HitRate);
        Assert.Equal(3, Assert.Single(run.Cases).FirstHitRank);
        Assert.Equal(1f / 3f, run.MeanReciprocalRank, 3);
    }

    [Fact]
    public async Task A_question_that_finds_nothing_counts_as_zero_in_the_average_rather_than_being_left_out()
    {
        await SaveSetAsync(
            new EvaluationCase { Question = "refunds", ExpectedDocumentIds = ["refunds"] },
            new EvaluationCase { Question = "missing", ExpectedDocumentIds = ["not-indexed"] });

        var run = await Evaluator.RunAsync("set1", cancellationToken: Ct);

        // Otherwise a configuration that answers one question perfectly and the rest not at all scores a
        // perfect MRR.
        Assert.Equal(0.5f, run.HitRate);
        Assert.Equal(0.5f, run.MeanReciprocalRank, 3);
    }

    [Fact]
    public async Task A_case_expecting_nothing_in_particular_passes_when_anything_comes_back()
    {
        await SaveSetAsync(new EvaluationCase { Question = "refunds" });

        // Such a case measures that the base is not empty, and nothing more. Worth allowing, worth saying.
        Assert.Equal(1f, (await Evaluator.RunAsync("set1", cancellationToken: Ct)).HitRate);
    }

    // ---------- comparing two configurations ----------

    [Fact]
    public async Task The_same_set_can_be_run_twice_with_different_settings_and_both_kept()
    {
        await SaveSetAsync(new EvaluationCase { Question = "refunds", ExpectedDocumentIds = ["refunds"] });

        await Evaluator.RunAsync("set1", new EvaluationOptions { Label = "vectors only", Retrieval = new RetrievalOptions { Mode = RetrievalMode.Vector } }, Ct);
        await Evaluator.RunAsync("set1", new EvaluationOptions { Label = "hybrid" }, Ct);

        // The whole point: the difference between two runs is evidence, where one number on its own is a
        // number whose meaning depends on how the questions were written.
        var runs = await Evaluator.ListRunsAsync("set1", cancellationToken: Ct);

        Assert.Equal(2, runs.Count);
        Assert.Equal(["hybrid", "vectors only"], runs.Select(r => r.Label));
        Assert.Equal(RetrievalMode.Vector, runs[1].Retrieval!.Mode);
    }

    // ---------- being told what is wrong ----------

    [Fact]
    public async Task A_set_for_a_knowledge_base_that_does_not_exist_is_refused()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Evaluator.SaveSetAsync(new EvaluationSet { Id = "s", Name = "S", KnowledgeBaseId = "nope" }, Ct));

        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_an_empty_set_says_so_rather_than_reporting_a_perfect_score()
    {
        await SaveSetAsync();

        // Zero questions, all of them passing, is the most misleading number a report like this can give.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() => Evaluator.RunAsync("set1", cancellationToken: Ct));

        Assert.Contains("no questions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_a_set_that_does_not_exist_says_so()
    {
        await Assert.ThrowsAsync<NetCoreAIException>(() => Evaluator.RunAsync("nope", cancellationToken: Ct));
    }

    [Fact]
    public async Task Deleting_a_set_takes_its_runs_with_it()
    {
        await SaveSetAsync(new EvaluationCase { Question = "refunds", ExpectedDocumentIds = ["refunds"] });
        await Evaluator.RunAsync("set1", cancellationToken: Ct);

        await Evaluator.DeleteSetAsync("set1", Ct);

        Assert.Empty(await Evaluator.ListRunsAsync("set1", cancellationToken: Ct));
    }

    // ---------- the judge ----------

    [Theory]
    [InlineData("0.8|the second claim is not in the passages", 0.8f, "the second claim is not in the passages")]
    [InlineData("1|everything is supported", 1f, "everything is supported")]
    [InlineData("0", 0f, null)]
    [InlineData("0,75|comma decimal", 0.75f, "comma decimal")]
    public void A_judges_reply_is_read_into_a_score_and_a_reason(string reply, float score, string? reason)
    {
        var (parsed, parsedReason) = KnowledgeEvaluator.Parse(reply);

        Assert.Equal(score, parsed);
        Assert.Equal(reason, parsedReason);
    }

    [Theory]
    [InlineData("I think the answer is mostly fine really")]
    [InlineData("")]
    [InlineData(null)]
    public void A_judge_that_rambled_leaves_a_gap_rather_than_a_zero(string? reply)
    {
        // Scoring an unparseable reply as "completely unsupported" would make a chatty model look like a
        // retrieval problem.
        Assert.Null(KnowledgeEvaluator.Parse(reply).Score);
    }

    [Fact]
    public void A_score_outside_the_range_is_brought_back_into_it()
    {
        Assert.Equal(1f, KnowledgeEvaluator.Parse("5|very confident indeed").Score);
    }

    /// <summary>Embeddings that are always the same vector, so no model has to be downloaded.</summary>
    private sealed class FixedEmbeddings : IChatClientFactory
    {
        public Microsoft.Extensions.AI.IChatClient Get(string idOrAlias = ModelAlias.Default) =>
            throw new NotSupportedException("These tests do not generate answers.");

        public bool TryGet(string idOrAlias, out Microsoft.Extensions.AI.IChatClient? client)
        {
            client = null;
            return false;
        }

        public Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> GetEmbeddingGenerator(
            string idOrAlias = ModelAlias.Embed) => new Generator();

        private sealed class Generator : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
        {
            public Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
                IEnumerable<string> values,
                Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>(
                    [.. values.Select(_ => new Microsoft.Extensions.AI.Embedding<float>(new float[] { 1f, 0f }))]));

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
