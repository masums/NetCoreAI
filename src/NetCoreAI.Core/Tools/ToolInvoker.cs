using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Tools;

/// <summary>What a tool call produced, as the model will see it.</summary>
/// <param name="Output">The text handed back to the model.</param>
/// <param name="Success">Whether the call did what it was asked.</param>
/// <param name="StatusCode">HTTP status, when the call was made over HTTP.</param>
/// <param name="ElapsedMs">How long it took, for the trace.</param>
public sealed record ToolCallResult(string Output, bool Success, int? StatusCode = null, long ElapsedMs = 0);

/// <summary>Makes the call a tool describes.</summary>
public interface IToolInvoker
{
    Task<ToolCallResult> InvokeAsync(
        ToolDefinition tool,
        IReadOnlyDictionary<string, object?>? arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Invokes a tool over HTTP — this host by loopback, or another service directly.
/// </summary>
/// <remarks>
/// A failed call is reported to the model as text rather than thrown. A model that is told "that returned
/// 404" can try something else or say it could not find the record; an exception ends the turn and the
/// person asking sees nothing useful.
/// </remarks>
internal sealed class ToolInvoker(
    IHttpClientFactory factory,
    IInProcessToolTransport inProcess,
    Microsoft.Extensions.Options.IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<ToolInvoker> logger) : IToolInvoker
{
    /// <summary>Named so a host can add handlers — a proxy, a certificate, a retry policy — to tool traffic alone.</summary>
    public const string HttpClientName = "NetCoreAI.Tools";

    public async Task<ToolCallResult> InvokeAsync(
        ToolDefinition tool,
        IReadOnlyDictionary<string, object?>? arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);

        if (!tool.Enabled)
        {
            return new ToolCallResult($"The tool '{tool.Name}' is turned off.", Success: false);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var activity = NetCoreAI.Telemetry.NetCoreAITelemetry.ToolCallSpan(tool.Name, tool.Kind.ToString(), out var ownsSpan);
        try
        {
            var bound = ToolBinding.Bind(tool, arguments, context);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(tool.TimeoutSeconds, 1, 600)));

            // Both paths answer with a status and a body, and everything after this point treats them the
            // same: whichever mode a tool uses must not change what the model is told.
            var (status, body) = tool.InvocationMode == ToolInvocationMode.InProcess
                ? await InProcessAsync(tool, bound, context, timeout.Token).ConfigureAwait(false)
                : await OverHttpAsync(tool, bound, context, timeout.Token).ConfigureAwait(false);

            var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (status is < 200 or > 299)
            {
                // The status is part of the answer: 403 means the caller may not do this, and the model
                // should say so rather than retrying or inventing a result.
                logger.LogInformation("Tool {Name} returned {Status}.", tool.Name, status);
                return Record(tool, activity, new ToolCallResult(
                    $"The call failed with HTTP {status}. {Truncate(body, 1024)}".TrimEnd(),
                    Success: false,
                    status,
                    elapsed));
            }

            return Record(tool, activity, new ToolCallResult(Shape(tool, body), Success: true, status, elapsed));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Record(tool, activity, new ToolCallResult(
                $"The call took longer than {tool.TimeoutSeconds} seconds and was abandoned.",
                Success: false,
                ElapsedMs: (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        catch (NetCoreAIException ex)
        {
            // A binding failure: something the host was meant to supply was missing. The model cannot fix
            // it, so it is told plainly rather than being invited to guess at arguments.
            return Record(tool, activity, new ToolCallResult(ex.Message, Success: false, ElapsedMs: (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Tool {Name} could not be reached.", tool.Name);
            return Record(tool, activity, new ToolCallResult($"The service could not be reached: {ex.Message}", Success: false, ElapsedMs: (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        finally
        {
            // Only ended by whoever started it: ending somebody else's span would cut their trace short.
            if (ownsSpan)
            {
                activity?.Dispose();
            }
        }
    }

    /// <summary>
    /// Puts the outcome on the span and the meters.
    /// </summary>
    /// <remarks>
    /// The arguments and the response are left off deliberately. A tool call carries whatever the caller
    /// was asking about — an order number, a customer, a medical record — and telemetry goes wherever the
    /// host exports it. What is counted is that a call happened, to what, and whether it worked.
    /// </remarks>
    private static ToolCallResult Record(ToolDefinition tool, System.Diagnostics.Activity? activity, ToolCallResult result)
    {
        var tags = new TagList
        {
            { "tool", tool.Name },
            { "kind", tool.Kind.ToString() },
            { "success", result.Success },
        };

        NetCoreAI.Telemetry.NetCoreAITelemetry.ToolCalls.Add(1, tags);
        NetCoreAI.Telemetry.NetCoreAITelemetry.ToolCallDuration.Record(result.ElapsedMs, tags);

        activity?.SetTag("http.response.status_code", result.StatusCode);
        NetCoreAI.Telemetry.NetCoreAITelemetry.Finish(activity, result.Success, result.Success ? null : result.Output);
        return result;
    }

    private async Task<(int Status, string Body)> OverHttpAsync(ToolDefinition tool, BoundArguments bound, ToolCallContext context, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(tool, bound, context, options.CurrentValue.Tools.BaseAddress);
        using var response = await factory.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<(int Status, string Body)> InProcessAsync(ToolDefinition tool, BoundArguments bound, ToolCallContext context, CancellationToken cancellationToken)
    {
        var response = await inProcess.SendAsync(tool, bound, context, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, response.Body);
    }

    private static HttpRequestMessage BuildRequest(ToolDefinition tool, BoundArguments bound, ToolCallContext context, Uri? configured)
    {
        var route = tool.Route ?? "/";
        foreach (var (name, value) in bound.Route)
        {
            // Route constraints travel in the pattern ({id:guid}); the value replaces the whole segment.
            route = System.Text.RegularExpressions.Regex.Replace(
                route,
                @"\{" + System.Text.RegularExpressions.Regex.Escape(name) + @"(:[^}]*)?\??\}",
                Uri.EscapeDataString(value),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        }

        var url = Base(tool, context, configured) + "/" + route.TrimStart('/');
        if (bound.Query.Count > 0)
        {
            url += "?" + string.Join('&', bound.Query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        }

        var request = new HttpRequestMessage(new HttpMethod(tool.Method), url);
        foreach (var (name, value) in bound.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (bound.Body is { } body)
        {
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        }

        // The caller's credentials are for this host. Forwarding them to an external service would hand
        // them to whoever runs it, so only a loopback or in-process call carries them.
        if (tool.ForwardCallerCredentials
            && tool.InvocationMode != ToolInvocationMode.HttpExternal
            && context.AuthorizationHeader is { Length: > 0 } authorization
            && AuthenticationHeaderValue.TryParse(authorization, out var parsed))
        {
            request.Headers.Authorization = parsed;
        }

        return request;
    }

    private static string Base(ToolDefinition tool, ToolCallContext context, Uri? configured)
    {
        if (tool.InvocationMode == ToolInvocationMode.HttpExternal)
        {
            return tool.BaseUrl?.TrimEnd('/')
                ?? throw new NetCoreAIException($"'{tool.Name}' is an external tool with no base URL, so there is nowhere to send the call.");
        }

        // The request that started the run knows the address the host is actually reached on, which is the
        // better answer behind a proxy; configuration is the fallback for runs with no request behind them.
        return (context.BaseAddress ?? configured)?.ToString().TrimEnd('/')
            ?? throw new NetCoreAIException(
                $"'{tool.Name}' calls this host back, but the host's own address is not known here. Set NetCoreAI:Tools:BaseAddress, or run the tool in-process.");
    }

    /// <summary>Cuts the response down to what the definition says is worth returning.</summary>
    private static string Shape(ToolDefinition tool, string body)
    {
        var text = body;
        if (tool.Response.SelectPath is { Length: > 0 } path)
        {
            text = Select(body, path) ?? body;
        }

        if (text.Length <= tool.Response.MaxBytes)
        {
            return text;
        }

        var cut = Truncate(text, tool.Response.MaxBytes);

        // A model reading a cut-off JSON object as though it were whole will answer confidently from half
        // a list. Saying it was cut is the difference between a wrong answer and a hedged one.
        return tool.Response.NoteTruncation
            ? cut + $"\n\n[Response cut at {tool.Response.MaxBytes} characters; there was more.]"
            : cut;
    }

    private static string? Select(string body, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var current = document.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
        }
        catch (JsonException)
        {
            // Not JSON, or not the shape the path expects. The whole body is a better answer than nothing.
            return null;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
