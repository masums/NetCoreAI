using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NetCoreAI.Knowledge;

namespace NetCoreAI.Agents;

/// <summary>Managing agents, and running them.</summary>
public interface IAgentService
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<AgentDefinition?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates an agent, refusing one that could not run.</summary>
    Task<AgentDefinition> SaveAsync(AgentDefinition agent, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<AgentResponse> RunAsync(string agentId, AgentRequest request, AgentCaller caller, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentEvent> RunStreamingAsync(string agentId, AgentRequest request, AgentCaller caller, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunTrace>> ListRunsAsync(string? agentId = null, int limit = 50, CancellationToken cancellationToken = default);

    Task<RunTrace?> GetRunAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Who is running an agent, and how their host can be called back.</summary>
public sealed record AgentCaller
{
    public ClaimsPrincipal? User { get; init; }

    /// <summary>Where this host answers itself, for tools invoked by loopback.</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>The caller's own Authorization header, forwarded to tools that say to.</summary>
    public string? AuthorizationHeader { get; init; }

    public string? UserId { get; init; }
}

/// <summary>
/// Runs agents, and writes down what each run did.
/// </summary>
/// <remarks>
/// Every run produces a trace whether it succeeded or not. A wrong answer is impossible to explain
/// afterwards without one — which passages were retrieved, which tools were called, with what arguments —
/// and that explanation is the difference between fixing a prompt and guessing at it.
/// </remarks>
internal sealed partial class AgentService(
    IMetadataStore store,
    IAgentEngine engine,
    IModelRegistry registry,
    NetCoreAI.Telemetry.ICostEstimator costs,
    NetCoreAI.Guardrails.IGuardrailService guardrails,
    Microsoft.Extensions.Options.IOptions<NetCoreAIOptions> options,
    ILogger<AgentService> logger) : IAgentService
{
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9._-]{0,63}$")]
    private static partial Regex IdPattern { get; }

    /// <summary>A tool result is kept in the trace, but a 16 KB one would make the trace the biggest row.</summary>
    private const int TraceValueLimit = 2048;

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        store.Agents.ListAsync(cancellationToken);

    public Task<AgentDefinition?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        store.Agents.GetAsync(id, cancellationToken);

    public async Task<AgentDefinition> SaveAsync(AgentDefinition agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (!IdPattern.IsMatch(agent.Id))
        {
            throw new NetCoreAIException($"'{agent.Id}' is not a usable agent id. Use a letter followed by letters, digits, dots, dashes or underscores.");
        }

        if (string.IsNullOrWhiteSpace(agent.Name))
        {
            throw new NetCoreAIException("An agent needs a name.");
        }

        if (agent.OutputMode == AgentOutputMode.Json && agent.OutputSchema is { Length: > 0 } schema)
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(schema);
            }
            catch (System.Text.Json.JsonException ex)
            {
                // Caught here rather than at the first run, where it would look like a model failure.
                throw new NetCoreAIException($"The output schema is not valid JSON: {ex.Message}", ex);
            }
        }

        var saved = agent with { UpdatedAt = DateTimeOffset.UtcNow };
        await store.Agents.UpsertAsync(saved, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Saved agent {Name} ({Id}).", saved.Name, saved.Id);
        return saved;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        store.Agents.DeleteAsync(id, cancellationToken);

    public Task<IReadOnlyList<RunTrace>> ListRunsAsync(string? agentId = null, int limit = 50, CancellationToken cancellationToken = default) =>
        store.Runs.ListAsync(agentId, limit, cancellationToken);

    public Task<RunTrace?> GetRunAsync(string id, CancellationToken cancellationToken = default) =>
        store.Runs.GetAsync(id, cancellationToken);

    public async Task<AgentResponse> RunAsync(string agentId, AgentRequest request, AgentCaller caller, CancellationToken cancellationToken = default)
    {
        AgentResponse? final = null;
        await foreach (var evt in RunStreamingAsync(agentId, request, caller, cancellationToken).ConfigureAwait(false))
        {
            if (evt.Type == AgentEvent.DoneType)
            {
                final = evt.Response;
            }
            else if (evt.Type == AgentEvent.ErrorType)
            {
                throw new NetCoreAIException(evt.Error ?? "The agent run failed.");
            }
        }

        return final ?? throw new NetCoreAIException("The agent run produced no answer.");
    }

    public async IAsyncEnumerable<AgentEvent> RunStreamingAsync(
        string agentId,
        AgentRequest request,
        AgentCaller caller,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var agent = await store.Agents.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            yield return new AgentEvent(AgentEvent.ErrorType) { Error = $"No agent with id '{agentId}'." };
            yield break;
        }

        if (!agent.Enabled)
        {
            yield return new AgentEvent(AgentEvent.ErrorType) { Error = $"The agent '{agent.Name}' is turned off." };
            yield break;
        }

        if (!Allowed(agent, caller.User))
        {
            // Named as "not allowed" rather than "not found": the caller can see the agent in a list, so
            // pretending it does not exist would only be confusing.
            yield return new AgentEvent(AgentEvent.ErrorType) { Error = $"You are not allowed to run '{agent.Name}'." };
            yield break;
        }

        await foreach (var evt in RunCoreAsync(agent, request, caller, cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    private async IAsyncEnumerable<AgentEvent> RunCoreAsync(
        AgentDefinition agent,
        AgentRequest request,
        AgentCaller caller,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var activity = NetCoreAI.Telemetry.NetCoreAITelemetry.StartAgentRun(agent.Id, agent.Name, agent.Model);
        var steps = new List<RunStep>();
        var run = new RunTrace
        {
            Id = Guid.NewGuid().ToString("N"),
            AgentId = agent.Id,
            SessionId = request.SessionId,
            UserId = caller.UserId,
            Input = request.Message,
        };

        // The run's own deadline, separate from the caller's: a caller who waits forever should not make
        // an agent run forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(agent.Limits.RunTimeoutSeconds, 1, 3600)));
        var ct = deadline.Token;

        var context = new ToolCallContext
        {
            User = caller.User,
            RequestMetadata = request.Metadata,
            BaseAddress = caller.BaseAddress,
            AuthorizationHeader = caller.AuthorizationHeader,
        };

        // The agent's own rules, or the host's defaults when it carries none.
        var policy = agent.Guardrails ?? options.Value.Guardrails;

        if (guardrails.CheckBudget(policy, agent.Id, request.SessionId, caller.UserId) is { } overspent)
        {
            await FailAsync(run, steps, overspent, started, cancellationToken).ConfigureAwait(false);
            Record(agent, activity, false, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, overspent);
            yield return new AgentEvent(AgentEvent.ErrorType) { Error = overspent };
            yield break;
        }

        // Checked before retrieval and before any model sees it, which is the last point at which masked
        // data has not yet left the process.
        var input = guardrails.CheckInput(policy, request.Message);
        foreach (var finding in input.Findings)
        {
            steps.Add(new RunStep(RunStep.GuardrailKind, finding.Rule)
            {
                Output = finding.Detail,
                Success = finding.Action != NetCoreAI.Guardrails.GuardrailAction.Block,
            });
        }

        if (input.Blocked)
        {
            // Recorded as a run like any other. A refusal nobody can look up afterwards is a rule nobody
            // can tune, and the first thing asked about one is always "what did they actually send?".
            await FailAsync(run, steps, input.BlockedReason!, started, cancellationToken).ConfigureAwait(false);
            Record(agent, activity, false, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, input.BlockedReason);
            NetCoreAI.Telemetry.NetCoreAITelemetry.GuardrailBlocks.Add(1, new System.Diagnostics.TagList { { "agent", agent.Id } });
            yield return new AgentEvent(AgentEvent.ErrorType) { Error = input.BlockedReason };
            yield break;
        }

        request = request with { Message = input.Text };
        run = run with { Input = input.Text };

        // Narrowed before the pipeline is built, so a tool this caller's role may not use is never put in
        // front of the model at all rather than offered and refused on use.
        var permitted = guardrails.AllowedTools(policy, agent.ToolIds, caller.User);
        var effective = permitted.Count == agent.ToolIds.Count ? agent : agent with { ToolIds = permitted };

        AgentPipeline? pipeline = null;
        List<ChatMessage>? messages = null;
        string? setupError = null;
        try
        {
            pipeline = await engine.BuildAsync(effective, context, ct).ConfigureAwait(false);
            messages = await MessagesAsync(effective, request, caller, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            setupError = ex.Message;
        }

        if (setupError is not null || pipeline is null || messages is null)
        {
            // A model that cannot be served, or a knowledge base that has gone. Recorded as a run so the
            // failure is visible in the same place as every other, rather than only in the logs.
            var reason = setupError ?? "The agent could not be prepared.";
            await FailAsync(run, steps, reason, started, cancellationToken).ConfigureAwait(false);
            Record(agent, activity, false, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, reason);
            yield return new AgentEvent(AgentEvent.ErrorType) { Error = reason };
            yield break;
        }

        if (policy.Budget.MaxTokensPerRun > 0)
        {
            // Applied as a cap on the answer rather than as a check afterwards: a budget only enforced
            // once the tokens are spent is a report, not a limit.
            pipeline.Options.MaxOutputTokens = Math.Min(
                pipeline.Options.MaxOutputTokens ?? int.MaxValue,
                policy.Budget.MaxTokensPerRun);
        }

        var outputGuard = new NetCoreAI.Guardrails.StreamingOutputGuard(guardrails, policy);
        var text = new System.Text.StringBuilder();
        var citations = new List<Citation>();
        UsageDetails? usage = null;
        string? error = null;

        var enumerator = pipeline.Client.GetStreamingResponseAsync(messages, pipeline.Options, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The run's own deadline, not the caller leaving. Partial text is kept: half an answer
                    // plus a reason is more use than nothing.
                    error = $"The run took longer than {agent.Limits.RunTimeoutSeconds} seconds and was stopped.";
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Agent {Agent} failed while generating.", agent.Name);
                    error = ex.Message;
                    break;
                }

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent t when !string.IsNullOrEmpty(t.Text):
                        {
                            var released = outputGuard.Push(t.Text);
                            if (released.Length > 0)
                            {
                                text.Append(released);
                                yield return new AgentEvent(AgentEvent.DeltaType) { Text = released };
                            }

                            break;
                        }

                        case UsageContent u:
                            usage = u.Details;
                            break;

                        case CitationContent c:
                            citations.AddRange(c.Citations);
                            steps.Add(new RunStep(RunStep.RetrievalKind, string.Join(", ", agent.Knowledge.Select(k => k.KnowledgeBaseId)))
                            {
                                Output = $"{c.Citations.Count} passage(s)",
                            });
                            yield return new AgentEvent(AgentEvent.CitationsType) { Citations = c.Citations };
                            break;

                        case FunctionCallContent call:
                        {
                            var step = new RunStep(RunStep.ToolKind, call.Name)
                            {
                                Input = Trim(System.Text.Json.JsonSerializer.Serialize(call.Arguments)),
                            };

                            steps.Add(step);
                            yield return new AgentEvent(AgentEvent.StepType) { Step = step };
                            break;
                        }

                        case FunctionResultContent result:
                        {
                            // Matched to the call by position: the loop runs them in order, and a result
                            // with no call before it would mean the pipeline changed underneath us.
                            var index = steps.FindLastIndex(s => s.Kind == RunStep.ToolKind && s.Output is null);
                            if (index >= 0)
                            {
                                steps[index] = steps[index] with { Output = Trim(result.Result?.ToString()) };
                            }

                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        var tail = outputGuard.Flush();
        if (tail.Length > 0)
        {
            text.Append(tail);
            yield return new AgentEvent(AgentEvent.DeltaType) { Text = tail };
        }

        foreach (var finding in outputGuard.Findings)
        {
            steps.Add(new RunStep(RunStep.GuardrailKind, finding.Rule) { Output = "in the answer" });
        }

        var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var answer = text.ToString();

        // Priced from the descriptor the alias resolved to, so a run that fell back to a different model
        // is costed as that model rather than as the one the agent names.
        var descriptor = (await registry.GetAsync(agent.Model, CancellationToken.None).ConfigureAwait(false))?.Descriptor;
        var finished = run with
        {
            Output = answer,
            ModelId = descriptor?.Id ?? agent.Model,
            Steps = steps,
            Citations = citations,
            InputTokens = (int?)usage?.InputTokenCount,
            OutputTokens = (int?)usage?.OutputTokenCount,
            EstimatedCost = descriptor is null ? null : costs.Estimate(descriptor, usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0),
            ElapsedMs = elapsed,
            Success = error is null,
            Error = error,
        };

        await store.Runs.UpsertAsync(finished, CancellationToken.None).ConfigureAwait(false);

        guardrails.RecordUsage(
            agent.Id,
            request.SessionId,
            caller.UserId,
            (finished.InputTokens ?? 0) + (finished.OutputTokens ?? 0),
            finished.EstimatedCost ?? 0);

        activity?.SetTag("gen_ai.response.model", finished.ModelId);
        activity?.SetTag("gen_ai.usage.input_tokens", finished.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", finished.OutputTokens);
        activity?.SetTag("netcoreai.run.id", finished.Id);
        activity?.SetTag("netcoreai.run.tool_calls", steps.Count(s => s.Kind == RunStep.ToolKind));
        Record(agent, activity, finished.Success, elapsed, error);

        if (error is not null && answer.Length == 0)
        {
            yield return new AgentEvent(AgentEvent.ErrorType) { Error = error };
            yield break;
        }

        yield return new AgentEvent(AgentEvent.DoneType)
        {
            Response = new AgentResponse
            {
                Text = answer,
                RunId = finished.Id,
                SessionId = request.SessionId,
                Citations = citations,
                Steps = steps,
                ModelId = finished.ModelId,
                ElapsedMs = elapsed,
                Error = error,
            },
        };
    }

    /// <summary>
    /// Puts a run's outcome on its span and the meters.
    /// </summary>
    /// <remarks>
    /// The question and the answer are left off. A run carries whatever a person asked, telemetry goes
    /// wherever the host exports it, and a support conversation is not something to put there by default.
    /// The trace in the database holds the content; this holds the shape.
    /// </remarks>
    private static void Record(AgentDefinition agent, System.Diagnostics.Activity? activity, bool success, long elapsedMs, string? error)
    {
        var tags = new System.Diagnostics.TagList
        {
            { "agent", agent.Id },
            { "model", agent.Model },
            { "success", success },
        };

        NetCoreAI.Telemetry.NetCoreAITelemetry.AgentRuns.Add(1, tags);
        NetCoreAI.Telemetry.NetCoreAITelemetry.AgentRunDuration.Record(elapsedMs, tags);
        NetCoreAI.Telemetry.NetCoreAITelemetry.Finish(activity, success, error);
    }

    /// <summary>The messages to send: the rendered system prompt, the remembered window, then the question.</summary>
    private async Task<List<ChatMessage>> MessagesAsync(AgentDefinition agent, AgentRequest request, AgentCaller caller, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>();
        if (agent.SystemPrompt is { Length: > 0 } prompt)
        {
            messages.Add(new ChatMessage(ChatRole.System, PromptTemplate.Render(prompt, agent, caller.User, request.Metadata)));
        }

        if (agent.Memory.Enabled && request.SessionId is { Length: > 0 } sessionId)
        {
            var history = await store.Sessions.GetMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);

            // A window of whole turns from the end: context costs money on every call, and the oldest part
            // of a long conversation is rarely what the next answer needs.
            foreach (var message in history.TakeLast(Math.Clamp(agent.Memory.WindowTurns, 1, 100) * 2))
            {
                messages.Add(new ChatMessage(
                    message.Role == ChatRole.Assistant.Value ? ChatRole.Assistant : ChatRole.User,
                    message.Content));
            }
        }

        messages.Add(new ChatMessage(ChatRole.User, request.Message));
        return messages;
    }

    private async Task FailAsync(RunTrace run, List<RunStep> steps, string error, long started, CancellationToken cancellationToken)
    {
        steps.Add(new RunStep(RunStep.ErrorKind, "run") { Output = error, Success = false });
        await store.Runs.UpsertAsync(
            run with
            {
                Steps = steps,
                Success = false,
                Error = error,
                ElapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether this caller may run the agent at all.</summary>
    private static bool Allowed(AgentDefinition agent, ClaimsPrincipal? user) =>
        agent.AclTags.Count == 0 || AclTag.Allows(agent.AclTags, AgentEngine.CallerTags(user));

    private static string? Trim(string? value) =>
        value is null || value.Length <= TraceValueLimit
            ? value
            : value[..TraceValueLimit] + $"… [cut at {TraceValueLimit} characters]";
}
