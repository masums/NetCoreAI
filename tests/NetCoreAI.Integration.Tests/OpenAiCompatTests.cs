using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Agents;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Providers;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Serving OpenAI's wire format, so a tool already pointed at OpenAI can be pointed here by changing a
/// base URL and a key.
/// </summary>
/// <remarks>
/// A translation layer rather than a second API. Everything NetCoreAI can do that this shape cannot
/// express lives on the native API, and nothing here invents a field to carry it.
/// </remarks>
public sealed class OpenAiCompatTests : IAsyncLifetime
{
    private FakeOpenAIServer _openAI = default!;
    private WebApplication _app = default!;
    private string _dataDir = "";

    public async ValueTask InitializeAsync()
    {
        _openAI = await FakeOpenAIServer.StartAsync();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend();

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();

        var connection = await _app.Services.GetRequiredService<IConnectionManager>().SaveAsync(
            new ProviderConnection
            {
                Id = "fake",
                Name = "Fake",
                ProviderId = OpenAICompatibleProvider.ProviderId,
                BaseUrl = _openAI.BaseUrl,
            },
            "sk-test",
            Ct);

        await _app.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "default",
            Name = "Fake chat",
            Format = ModelFormat.Remote,
            ProviderId = OpenAICompatibleProvider.ProviderId,
            ConnectionId = connection.Id,
            RemoteModelId = "fake-chat",
            Capabilities = new ModelCapabilities(ModelCapability.Chat | ModelCapability.Embeddings),
        }, Ct);

        await _app.Services.GetRequiredService<IAgentService>().SaveAsync(
            new AgentDefinition { Id = "helper", Name = "Helper", Model = "default", SystemPrompt = "You are a helper." },
            Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _openAI.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _app.GetTestClient();

    private Task<HttpResponseMessage> ChatAsync(object body) =>
        Client.PostAsJsonAsync("/netcoreai/v1/chat/completions", body, Ct);

    private static object Ask(string model, string message, bool stream = false) =>
        new { model, stream, messages = new[] { new { role = "user", content = message } } };

    // ---------- the shape ----------

    [Fact]
    public async Task A_completion_comes_back_in_the_shape_a_client_expects()
    {
        var response = await ChatAsync(Ask("default", "hello"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("chatcmpl-", body.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("chat.completion", body.GetProperty("object").GetString());

        var choice = body.GetProperty("choices")[0];
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Contains("hello", choice.GetProperty("message").GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.True(body.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);
    }

    [Fact]
    public async Task A_streamed_completion_opens_with_the_role_and_ends_with_DONE()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/netcoreai/v1/chat/completions")
        {
            Content = JsonContent.Create(Ask("default", "hello", stream: true)),
        };

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        // More than one client library will not read any of the rest until it has seen a role-only first
        // chunk, which is what OpenAI sends.
        Assert.Contains("\"role\":\"assistant\"", text, StringComparison.Ordinal);
        Assert.Contains("chat.completion.chunk", text, StringComparison.Ordinal);
        Assert.Contains("\"finish_reason\":\"stop\"", text, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embeddings_come_back_as_a_list_with_an_index_each()
    {
        var response = await Client.PostAsJsonAsync(
            "/netcoreai/v1/embeddings",
            new { model = "default", input = new[] { "one", "two" } },
            Ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal("list", body.GetProperty("object").GetString());
        Assert.Equal(2, body.GetProperty("data").GetArrayLength());
        Assert.Equal(1, body.GetProperty("data")[1].GetProperty("index").GetInt32());
        Assert.True(body.GetProperty("data")[0].GetProperty("embedding").GetArrayLength() > 0);
    }

    [Fact]
    public async Task A_single_string_input_is_accepted_as_well_as_an_array()
    {
        var response = await Client.PostAsJsonAsync("/netcoreai/v1/embeddings", new { model = "default", input = "just one" }, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(1, body.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Models_lists_both_models_and_agents()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/netcoreai/v1/models", Ct);
        var ids = body.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();

        // A client that populates a model picker from this is how most people will find out they can
        // point at an agent.
        Assert.Contains("default", ids);
        Assert.Contains("helper", ids);
    }

    // ---------- pointing at an agent ----------

    [Fact]
    public async Task Naming_an_agent_as_the_model_runs_the_agent()
    {
        await ChatAsync(Ask("helper", "hello"));

        // The agent's system prompt reached the model, which a plain model call would not have sent.
        var system = _openAI.Requests[^1]["messages"]!.AsArray()[0]!["content"]!.ToString();
        Assert.Equal("You are a helper.", system);
    }

    [Fact]
    public async Task Only_the_last_user_message_is_the_question()
    {
        await ChatAsync(new
        {
            model = "helper",
            stream = false,
            messages = new[]
            {
                new { role = "user", content = "first question" },
                new { role = "assistant", content = "first answer" },
                new { role = "user", content = "second question" },
            },
        });

        // A client that resends the whole conversation every turn — which most do — would otherwise have
        // it counted twice: once as the agent's own memory and once as the question.
        var sent = _openAI.Requests[^1]["messages"]!.AsArray()[^1]!["content"]!.ToString();
        Assert.Equal("second question", sent);
    }

    [Fact]
    public async Task An_agent_can_be_streamed_too()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/netcoreai/v1/chat/completions")
        {
            Content = JsonContent.Create(Ask("helper", "hello", stream: true)),
        };

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("chat.completion.chunk", text, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);
    }

    // ---------- being forgiving, and being clear ----------

    [Fact]
    public async Task Fields_this_host_cannot_honour_are_ignored_rather_than_refused()
    {
        var response = await ChatAsync(new
        {
            model = "default",
            messages = new[] { new { role = "user", content = "hello" } },
            frequency_penalty = 0.5,
            presence_penalty = 0.2,
            logit_bias = new { },
            user = "somebody",
            seed = 42,
        });

        // A client sends these to everybody. Refusing the request over a field the model has no notion of
        // would make the compatibility layer useless for exactly the clients it exists for.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Content_given_as_parts_is_read_as_text()
    {
        var response = await ChatAsync(new
        {
            model = "default",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "the readable part" },
                        new { type = "image_url", image_url = new { url = "https://example.com/x.png" } },
                    },
                },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The image is dropped rather than refused: a host with no vision model cannot do anything useful
        // with it, and answering the text beats rejecting the message.
        Assert.Contains("the readable part", _openAI.Requests[^1]["messages"]!.AsArray()[^1]!["content"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_model_is_reported_in_the_envelope_a_client_reads()
    {
        var response = await ChatAsync(Ask("no-such-model", "hello"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Not a problem document. A client written against OpenAI reads error.message, and an RFC 9110
        // body here means every one of them reports "an error occurred".
        Assert.Contains("no-such-model", body.GetProperty("error").GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Equal("model_not_found", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":\"default\"}")]
    [InlineData("{\"model\":\"default\",\"messages\":[]}")]
    public async Task A_request_missing_what_it_needs_says_so(string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await Client.PostAsync(new Uri("/netcoreai/v1/chat/completions", UriKind.Relative), content, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request_error", body.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_developer_message_is_treated_as_a_system_message()
    {
        await ChatAsync(new
        {
            model = "default",
            messages = new[]
            {
                new { role = "developer", content = "be terse" },
                new { role = "user", content = "hello" },
            },
        });

        // OpenAI's newer name for the same thing. A client that has moved to it must not have its
        // instructions silently demoted to a user turn.
        Assert.Equal("system", _openAI.Requests[^1]["messages"]!.AsArray()[0]!["role"]!.ToString());
    }
}
