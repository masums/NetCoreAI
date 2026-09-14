using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NetCoreAI.Tools;

/// <summary>What a trial call did, in enough detail to work out why it did not do what was wanted.</summary>
/// <param name="Success">Whether the call succeeded.</param>
/// <param name="Output">What the model would have been given back.</param>
public sealed record ToolTestResult(bool Success, string Output)
{
    /// <summary>The arguments actually used, which is the interesting part when the model chose them.</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public int? StatusCode { get; init; }

    public long ElapsedMs { get; init; }

    /// <summary>Why no call was made, when none was.</summary>
    public string? Error { get; init; }

    /// <summary>What the model said instead of calling the tool, when it decided not to.</summary>
    public string? ModelSaid { get; init; }
}

/// <summary>Trying a tool out before an agent depends on it.</summary>
public interface IToolTester
{
    /// <summary>
    /// Calls a tool with the given arguments, or with arguments the model chooses from a sample prompt.
    /// </summary>
    /// <param name="toolId">Id or name of the tool.</param>
    /// <param name="arguments">Arguments to use; ignored when <paramref name="prompt"/> is given.</param>
    /// <param name="prompt">A question to hand the model, so it decides the arguments itself.</param>
    /// <param name="context">Who the trial call is made as.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ToolTestResult> TestAsync(
        string toolId,
        IReadOnlyDictionary<string, object?>? arguments,
        string? prompt,
        ToolCallContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs a tool once, on purpose.
/// </summary>
/// <remarks>
/// The prompt mode exists because most tool problems are not "the call failed" but "the model did not
/// understand what this tool was for". Handing it a real question and showing which arguments it chose —
/// or that it chose not to call at all — is the only way to see that before an agent depends on it.
/// </remarks>
internal sealed class ToolTester(IToolService tools, IToolRegistry registry, IChatClientFactory clients) : IToolTester
{
    public async Task<ToolTestResult> TestAsync(
        string toolId,
        IReadOnlyDictionary<string, object?>? arguments,
        string? prompt,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(context);

        if (await tools.GetAsync(toolId, cancellationToken).ConfigureAwait(false) is not { } definition)
        {
            throw new NetCoreAIException($"No tool with id or name '{toolId}'.");
        }

        var available = await registry.GetFunctionsAsync([definition.Id], context, cancellationToken).ConfigureAwait(false);
        if (available.Count == 0)
        {
            throw new NetCoreAIException(
                definition.Enabled
                    ? $"'{definition.Name}' is not available to this caller. An admin-only tool is withheld from everyone else, including here."
                    : $"'{definition.Name}' is turned off.");
        }

        var function = available[0];

        var chosen = arguments;
        string? modelSaid = null;

        if (prompt is { Length: > 0 })
        {
            var (fromModel, said) = await ChooseAsync(function, prompt, cancellationToken).ConfigureAwait(false);
            if (fromModel is null)
            {
                // Not a failure of the tool. The description is what the model reads to decide, so this is
                // the answer the person testing needs: it did not think this tool applied.
                return new ToolTestResult(false, string.Empty)
                {
                    ModelSaid = said,
                    Error = "The model did not call the tool for that prompt. Its description is what it reads to decide.",
                };
            }

            chosen = fromModel;
            modelSaid = said;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var call = new AIFunctionArguments();
        foreach (var (name, value) in chosen ?? new Dictionary<string, object?>(StringComparer.Ordinal))
        {
            call[name] = value;
        }

        var result = await function.InvokeAsync(call, cancellationToken).ConfigureAwait(false);

        return new ToolTestResult(true, result?.ToString() ?? string.Empty)
        {
            Arguments = chosen ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            ElapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            ModelSaid = modelSaid,
        };
    }

    /// <summary>
    /// Asks the model what it would call the tool with, without letting it call.
    /// </summary>
    /// <remarks>
    /// The model is offered a stand-in with the same name, description and schema as the real tool, which
    /// records the arguments and returns nothing. The chat client the factory hands out already runs the
    /// tool-invocation loop, so offering the real function would fire it there and again here — twice for
    /// one trial, and a side-effecting tool would do its work because somebody typed a sentence into a test
    /// box. With a stand-in, the call happens exactly once, below, under this method's control.
    /// </remarks>
    private async Task<(IReadOnlyDictionary<string, object?>? Arguments, string? Said)> ChooseAsync(
        AIFunction function,
        string prompt,
        CancellationToken cancellationToken)
    {
        var probe = new ProbeFunction(function);
        var response = await clients.Get().GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "Use the available tool when it fits the request. Do not ask for confirmation."),
                new ChatMessage(ChatRole.User, prompt),
            ],
            new ChatOptions { Tools = [probe], ToolMode = ChatToolMode.Auto, Temperature = 0 },
            cancellationToken).ConfigureAwait(false);

        if (probe.Arguments is { } recorded)
        {
            return (recorded, response.Text);
        }

        // Some clients hand the call back rather than invoking it. Either way the arguments are what matter.
        var call = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(c => string.Equals(c.Name, function.Name, StringComparison.Ordinal));

        return call?.Arguments is null
            ? (null, response.Text)
            : (call.Arguments.ToDictionary(a => a.Key, a => Plain(a.Value), StringComparer.Ordinal), response.Text);
    }

    /// <summary>Looks exactly like the tool to the model, and does nothing but remember what it was asked.</summary>
    private sealed class ProbeFunction(AIFunction real) : AIFunction
    {
        public IReadOnlyDictionary<string, object?>? Arguments { get; private set; }

        public override string Name => real.Name;

        public override string Description => real.Description;

        public override JsonElement JsonSchema => real.JsonSchema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Arguments = arguments?.ToDictionary(a => a.Key, a => Plain(a.Value), StringComparer.Ordinal)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            // The model may carry on after a tool call; this keeps it from reading a fabricated result as
            // real while still letting the turn finish.
            return ValueTask.FromResult<object?>("(the tool was not run: this was a test of which arguments you would choose)");
        }
    }

    /// <summary>JSON values as the plain values a reader expects to see in the arguments panel.</summary>
    private static object? Plain(object? value) => value is JsonElement element
        ? element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        }
        : value;
}
