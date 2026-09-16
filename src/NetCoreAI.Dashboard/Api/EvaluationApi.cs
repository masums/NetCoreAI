using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Knowledge;

namespace NetCoreAI.Dashboard.Api;

/// <summary>Measuring a knowledge base, and comparing two ways of searching it.</summary>
internal static class EvaluationApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var sets = api.MapGroup("/evaluations");

        sets.MapGet("/", async (IKnowledgeEvaluator evaluator, string? knowledgeBaseId, CancellationToken ct) =>
            Results.Ok(await evaluator.ListSetsAsync(knowledgeBaseId, ct))).WithName("NetCoreAI.Evaluations.List");

        sets.MapPut("/{id}", async (string id, EvaluationSet set, IKnowledgeEvaluator evaluator, CancellationToken ct) =>
            Results.Ok(await evaluator.SaveSetAsync(set with { Id = id }, ct))).WithName("NetCoreAI.Evaluations.Save");

        sets.MapDelete("/{id}", async (string id, IKnowledgeEvaluator evaluator, CancellationToken ct) =>
        {
            await evaluator.DeleteSetAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Evaluations.Delete");

        // The comparison: the same questions, different retrieval settings, twice.
        sets.MapPost("/{id}/run", async (string id, EvaluationOptions? options, IKnowledgeEvaluator evaluator, CancellationToken ct) =>
            Results.Ok(await evaluator.RunAsync(id, options, ct))).WithName("NetCoreAI.Evaluations.Run");

        sets.MapGet("/{id}/runs", async (string id, IKnowledgeEvaluator evaluator, int limit = 50, CancellationToken ct = default) =>
            Results.Ok(await evaluator.ListRunsAsync(id, limit, ct))).WithName("NetCoreAI.Evaluations.Runs");
    }
}
