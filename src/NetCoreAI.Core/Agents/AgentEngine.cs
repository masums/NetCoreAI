using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NetCoreAI.Knowledge;
using NetCoreAI.Tools;

namespace NetCoreAI.Agents;

/// <summary>Builds the chat client one agent run needs.</summary>
internal interface IAgentEngine
{
    /// <summary>
    /// The client for this agent and this caller: the model, wrapped so it answers from the agent's
    /// knowledge bases and can call the agent's tools.
    /// </summary>
    Task<AgentPipeline> BuildAsync(AgentDefinition agent, ToolCallContext context, CancellationToken cancellationToken = default);
}

/// <summary>The client for a run, and what was attached to it.</summary>
/// <param name="Client">What to send messages to.</param>
/// <param name="Options">Chat options carrying the tools and any structured-output schema.</param>
/// <param name="ToolCount">Tools the agent was actually given, which may be fewer than it names.</param>
internal sealed record AgentPipeline(IChatClient Client, ChatOptions Options, int ToolCount);

/// <summary>
/// Assembles model, retrieval and tools into one client.
/// </summary>
/// <remarks>
/// The order is deliberate. Retrieval wraps the model so passages are in front of it before it decides
/// anything, and the tool loop wraps that so a tool call can be made about what was retrieved. Reversing
/// them would let the model call tools before it had read the documents that say which tool to call.
/// </remarks>
internal sealed class AgentEngine(
    IChatClientFactory clients,
    IRagChatClientFactory rag,
    IToolRegistry tools,
    ILogger<AgentEngine> logger) : IAgentEngine
{
    public async Task<AgentPipeline> BuildAsync(AgentDefinition agent, ToolCallContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(context);

        var functions = agent.ToolIds.Count == 0
            ? []
            : await tools.GetFunctionsAsync(agent.ToolIds, context, cancellationToken).ConfigureAwait(false);

        if (functions.Count < agent.ToolIds.Count)
        {
            // Not fatal: a tool may have been deleted, disabled, or withheld from this caller. The run
            // still happens, and the trace records what it had.
            logger.LogInformation(
                "Agent {Agent} names {Named} tool(s) and was given {Given}: some are missing, disabled, or not available to this caller.",
                agent.Name, agent.ToolIds.Count, functions.Count);
        }

        var model = agent.Model;
        IChatClient client = agent.Knowledge.Count > 0
            ? rag.Create(model, new RagOptions
            {
                KnowledgeBaseIds = [.. agent.Knowledge.Select(k => k.KnowledgeBaseId)],

                // One agent, one retrieval setting: the first base that names one wins, because a single
                // query cannot be run with two different top-k values.
                Retrieval = agent.Knowledge.FirstOrDefault(k => k.Retrieval is not null)?.Retrieval,
                CallerTags = CallerTags(context.User),
            })
            : clients.Get(model);

        if (functions.Count > 0)
        {
            // The loop lives outside retrieval so a tool result can be read in the same turn, and its
            // iteration cap is the agent's rather than the library's default.
            client = new ChatClientBuilder(client)
                .UseFunctionInvocation(configure: invocation =>
                {
                    invocation.MaximumIterationsPerRequest = Math.Clamp(agent.Limits.MaxToolIterations, 1, 50);
                    invocation.IncludeDetailedErrors = false;
                })
                .Build();
        }

        return new AgentPipeline(client, Options(agent, functions), functions.Count);
    }

    private static ChatOptions Options(AgentDefinition agent, IReadOnlyList<AIFunction> functions)
    {
        var parameters = agent.Parameters;
        var options = new ChatOptions
        {
            Temperature = parameters.Temperature,
            TopP = parameters.TopP,
            TopK = parameters.TopK,
            MaxOutputTokens = parameters.MaxOutputTokens,
            FrequencyPenalty = parameters.FrequencyPenalty,
            PresencePenalty = parameters.PresencePenalty,
            Seed = parameters.Seed,
            StopSequences = parameters.StopSequences?.ToList(),
            Tools = functions.Count > 0 ? [.. functions] : null,
        };

        if (agent.OutputMode == AgentOutputMode.Json)
        {
            options.ResponseFormat = agent.OutputSchema is { Length: > 0 } schema
                ? ChatResponseFormat.ForJsonSchema(JsonSerializer.Deserialize<JsonElement>(schema))
                : ChatResponseFormat.Json;
        }

        return options;
    }

    /// <summary>
    /// The access tags retrieval should filter by.
    /// </summary>
    /// <remarks>
    /// An unauthenticated caller gets an empty list, never null: null means "no filtering at all", which
    /// would hand every restricted passage to anyone who could reach the agent.
    /// </remarks>
    internal static IReadOnlyList<string> CallerTags(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return [];
        }

        var tags = new List<string>();
        foreach (var claim in user.Claims)
        {
            var type = claim.Type.Contains('/', StringComparison.Ordinal)
                ? claim.Type[(claim.Type.LastIndexOf('/') + 1)..]
                : claim.Type;

            if (claim.Value is { Length: > 0 })
            {
                tags.Add(AclTag.From(type, claim.Value));
            }
        }

        return tags;
    }
}
