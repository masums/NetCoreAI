using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NetCoreAI.Telemetry;

/// <summary>Shared ActivitySource / Meter. Hosts subscribe with OpenTelemetry using these names.</summary>
public static class NetCoreAITelemetry
{
    public const string SourceName = "NetCoreAI";

    public static readonly ActivitySource ActivitySource = new(SourceName, ThisAssembly.Version);

    public static readonly Meter Meter = new(SourceName, ThisAssembly.Version);

    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("netcoreai.requests", description: "Chat/embedding requests by model and outcome.");

    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("netcoreai.errors", description: "Failed chat/embedding requests by model.");

    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("netcoreai.request.duration", unit: "ms");

    public static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("netcoreai.tokens.input");

    public static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("netcoreai.tokens.output");

    public static readonly UpDownCounter<long> LoadedModels = Meter.CreateUpDownCounter<long>("netcoreai.models.loaded");

    public static readonly UpDownCounter<long> ActiveGenerations = Meter.CreateUpDownCounter<long>("netcoreai.generations.active");

    public static readonly Counter<long> AgentRuns = Meter.CreateCounter<long>("netcoreai.agent.runs", description: "Agent runs by agent and outcome.");

    public static readonly Histogram<double> AgentRunDuration = Meter.CreateHistogram<double>("netcoreai.agent.run.duration", unit: "ms");

    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("netcoreai.tool.calls", description: "Tool invocations by tool and outcome.");

    public static readonly Histogram<double> ToolCallDuration = Meter.CreateHistogram<double>("netcoreai.tool.call.duration", unit: "ms");

    /// <summary>
    /// Starts a span for one agent run, named and tagged to the GenAI semantic conventions.
    /// </summary>
    /// <remarks>
    /// The conventions name the operation and the agent so a trace reads the same whichever framework
    /// produced it. Nothing here carries the question or the answer: a trace is exported to wherever the
    /// host sends telemetry, and a support conversation is not something to put there by default.
    /// </remarks>
    public static Activity? StartAgentRun(string agentId, string agentName, string? modelId)
    {
        var activity = ActivitySource.StartActivity($"invoke_agent {agentName}", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "invoke_agent");
        activity?.SetTag("gen_ai.agent.id", agentId);
        activity?.SetTag("gen_ai.agent.name", agentName);
        if (modelId is { Length: > 0 })
        {
            activity?.SetTag("gen_ai.request.model", modelId);
        }

        return activity;
    }

    /// <summary>
    /// The span for one tool call: the one already in progress, or a new one.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Extensions.AI</c>'s function-invoking client already emits an <c>execute_tool</c> span
    /// to the same conventions when it runs the tool loop. Starting another inside it produced two spans
    /// for one call — which double-counts in any latency dashboard and reads as two calls in a trace — so
    /// that one is enriched instead. A call made outside the loop (the tool tester, or a host invoking a
    /// tool directly) has no such span, and gets one.
    /// </remarks>
    /// <param name="toolName">The tool being called.</param>
    /// <param name="kind">Where the tool came from.</param>
    /// <param name="started">True when this call started the span and must therefore end it.</param>
    public static Activity? ToolCallSpan(string toolName, string kind, out bool started)
    {
        if (Activity.Current is { } current
            && current.GetTagItem("gen_ai.operation.name") as string == "execute_tool")
        {
            started = false;
            current.SetTag("netcoreai.tool.kind", kind);
            return current;
        }

        started = true;
        var activity = ActivitySource.StartActivity($"execute_tool {toolName}", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "execute_tool");
        activity?.SetTag("gen_ai.tool.name", toolName);
        activity?.SetTag("gen_ai.tool.type", kind);
        return activity;
    }

    /// <summary>Records the outcome on a span, including the error when there was one.</summary>
    public static void Finish(Activity? activity, bool success, string? error = null)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, success ? null : error);
        if (!success && error is { Length: > 0 })
        {
            activity.SetTag("error.type", error);
        }
    }
}

internal static class ThisAssembly
{
    public static readonly string Version =
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
