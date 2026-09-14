using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetCoreAI.Client;

/// <summary>
/// <see cref="IAgentClient"/> against a remote NetCoreAI host.
/// </summary>
/// <remarks>
/// The same interface the in-process client implements, so an application moves between hosting NetCoreAI
/// and calling one without changing how it asks. The caller is whoever the API key or signed-in identity
/// says — a client cannot declare it, because it would then be choosing its own permissions.
/// </remarks>
internal sealed class HttpAgentClient(IHttpClientFactory factory) : IAgentClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private HttpClient Http => factory.CreateClient(NetCoreAIClientServiceCollectionExtensions.HttpClientName);

    public async Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/agents", content: null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AgentDefinition>>(Json, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<AgentResponse> RunAsync(string agentId, AgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);

        using var body = JsonContent.Create(request, options: Json);
        using var response = await SendAsync(HttpMethod.Post, $"api/agents/{Uri.EscapeDataString(agentId)}/run", body, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<AgentResponse>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new NetCoreAIException("The host ran the agent but returned nothing.");
    }

    public async IAsyncEnumerable<AgentEvent> RunStreamingAsync(
        string agentId,
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);

        using var body = JsonContent.Create(request, options: Json);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/agents/{Uri.EscapeDataString(agentId)}/run/stream") { Content = body };

        HttpResponseMessage response;
        try
        {
            // Headers only: the point of streaming is to read events as they arrive rather than after the
            // run has finished.
            response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NetCoreAIException($"Could not reach the NetCoreAI host at {Http.BaseAddress}: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new NetCoreAIException($"The NetCoreAI host refused the run ({(int)response.StatusCode}): {await ProblemAsync(response, cancellationToken).ConfigureAwait(false)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? line;
            string? data = null;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                    continue;
                }

                // A blank line ends an event. The event name is carried inside the payload too, so only
                // the data line has to be kept.
                if (line.Length == 0 && data is { Length: > 0 })
                {
                    var evt = Parse(data);
                    data = null;
                    if (evt is not null)
                    {
                        yield return evt;
                    }
                }
            }
        }
    }

    private static AgentEvent? Parse(string data)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentEvent>(data, Json);
        }
        catch (JsonException)
        {
            // One unreadable event should not abandon a run that is otherwise producing an answer.
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NetCoreAIException($"Could not reach the NetCoreAI host at {Http.BaseAddress}: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var detail = await ProblemAsync(response, cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        throw new NetCoreAIException(
            status == HttpStatusCode.TooManyRequests
                ? $"The NetCoreAI host is rate-limiting this key: {detail}"
                : $"The NetCoreAI host refused the request ({(int)status}): {detail}");
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body is { Length: > 0 } && JsonSerializer.Deserialize<ProblemBody>(body, Json) is { } problem)
            {
                return problem.Detail ?? problem.Error ?? problem.Title ?? body;
            }

            return body is { Length: > 0 } ? body : response.ReasonPhrase ?? "no detail given";
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "no detail given";
        }
    }

    private sealed record ProblemBody
    {
        public string? Title { get; init; }

        public string? Detail { get; init; }

        public string? Error { get; init; }
    }
}
