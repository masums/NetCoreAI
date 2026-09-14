using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace NetCoreAI.Telemetry;

/// <summary>
/// Counts every generation: the OpenTelemetry meter for a host's monitoring stack, and the in-process
/// usage tracker that feeds the dashboard. Sits outside the provider so a failure is still counted.
/// </summary>
internal sealed class MetricsChatClient(IChatClient inner, ModelDescriptor model, IUsageTracker tracker, ICostEstimator costs) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        NetCoreAITelemetry.ActiveGenerations.Add(1, Tag);
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            Record(true, stopwatch.Elapsed.TotalMilliseconds, response.Usage);
            return response;
        }
        catch (Exception) when (Fail(stopwatch))
        {
            throw;   // never reached: the filter returns false after recording
        }
        finally
        {
            NetCoreAITelemetry.ActiveGenerations.Add(-1, Tag);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        UsageDetails? usage = null;
        var failed = false;

        NetCoreAITelemetry.ActiveGenerations.Add(1, Tag);
        var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
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
                catch (OperationCanceledException)
                {
                    // A user pressing stop is not an error; what was generated still counts.
                    break;
                }
                catch (Exception)
                {
                    failed = true;
                    throw;
                }

                usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            stopwatch.Stop();
            NetCoreAITelemetry.ActiveGenerations.Add(-1, Tag);
            Record(!failed, stopwatch.Elapsed.TotalMilliseconds, usage);
        }
    }

    /// <summary>Records the failure from inside an exception filter, so the original stack is preserved.</summary>
    private bool Fail(Stopwatch stopwatch)
    {
        stopwatch.Stop();
        Record(false, stopwatch.Elapsed.TotalMilliseconds, null);
        return false;
    }

    private void Record(bool success, double elapsedMs, UsageDetails? usage)
    {
        var input = usage?.InputTokenCount ?? 0;
        var output = usage?.OutputTokenCount ?? 0;

        NetCoreAITelemetry.Requests.Add(1, Tag, new KeyValuePair<string, object?>("outcome", success ? "ok" : "error"));
        NetCoreAITelemetry.RequestDuration.Record(elapsedMs, Tag);
        if (!success)
        {
            NetCoreAITelemetry.Errors.Add(1, Tag);
        }

        if (input > 0)
        {
            NetCoreAITelemetry.InputTokens.Add(input, Tag);
        }

        if (output > 0)
        {
            NetCoreAITelemetry.OutputTokens.Add(output, Tag);
        }

        tracker.Record(new UsageEvent(model.Id, success, elapsedMs, input, output, costs.Estimate(model, input, output)));
    }

    private KeyValuePair<string, object?> Tag => new("model", model.Id);
}
