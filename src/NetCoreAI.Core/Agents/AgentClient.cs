using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Agents;

/// <summary>
/// The in-process <see cref="IAgentClient"/>: what application code injects to run an agent.
/// </summary>
/// <remarks>
/// The caller is taken from the current request when there is one, so an agent run from inside a request
/// acts as whoever made it — its tools and its retrieval both see that identity. Called from a background
/// service there is no request and no user, which means no ACL-restricted document and no agent with access
/// tags. That is the safe default: code that means to act for somebody should say who through the HTTP API
/// or by running the service directly.
/// </remarks>
internal sealed class AgentClient(
    IAgentService agents,
    IServiceProvider services,
    IOptionsMonitor<NetCoreAIOptions> options) : IAgentClient
{
    public Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        agents.ListAsync(cancellationToken);

    public Task<AgentResponse> RunAsync(string agentId, AgentRequest request, CancellationToken cancellationToken = default) =>
        agents.RunAsync(agentId, request, Caller(), cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunStreamingAsync(string agentId, AgentRequest request, CancellationToken cancellationToken = default) =>
        agents.RunStreamingAsync(agentId, request, Caller(), cancellationToken);

    private AgentCaller Caller()
    {
        // Resolved rather than injected: a console or worker host never registers the accessor, and
        // NetCoreAI must not stop such a host from starting over a value it only sometimes has.
        var context = services.GetService<IHttpContextAccessor>()?.HttpContext;
        return new AgentCaller
        {
            User = context?.User,
            UserId = context?.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity.Name
                : null,
            AuthorizationHeader = context?.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null,

            // The live request knows the address this host is reached on, which is what a loopback tool
            // call needs behind a proxy; configuration is the fallback for runs with no request behind
            // them. A context with no Host — a synthetic one, or a malformed request — would otherwise
            // produce "http://" and throw, taking the run down over an address it did not need.
            BaseAddress = Address(context) ?? options.CurrentValue.Tools.BaseAddress,
        };
    }

    private static Uri? Address(HttpContext? context) =>
        context?.Request.Host.HasValue == true
        && Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out var address)
            ? address
            : null;
}

/// <summary>Convenience over <see cref="IAgentClient"/> for the common shapes of a run.</summary>
public static class AgentClientExtensions
{
    /// <summary>Runs an agent with a single question and returns what it said.</summary>
    public static async Task<string> AskAsync(this IAgentClient client, string agentId, string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return (await client.RunAsync(agentId, new AgentRequest { Message = message }, cancellationToken).ConfigureAwait(false)).Text;
    }

    /// <summary>Runs an agent and yields only its text, for a caller that wants to stream an answer on.</summary>
    public static async IAsyncEnumerable<string> StreamTextAsync(
        this IAgentClient client,
        string agentId,
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        await foreach (var evt in client.RunStreamingAsync(agentId, new AgentRequest { Message = message }, cancellationToken).ConfigureAwait(false))
        {
            if (evt.Type == AgentEvent.DeltaType && evt.Text is { Length: > 0 } text)
            {
                yield return text;
            }
            else if (evt.Type == AgentEvent.ErrorType)
            {
                throw new NetCoreAIException(evt.Error ?? "The agent run failed.");
            }
        }
    }
}
