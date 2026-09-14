using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace NetCoreAI.Integration.Tests.Acceptance;

/// <summary>
/// A tripwire on somebody else's bug.
/// </summary>
/// <remarks>
/// Gemini's OpenAI-compatible endpoint returns a <c>thought_signature</c> inside each tool call and
/// refuses the next turn without it. <c>Microsoft.Extensions.AI</c>'s OpenAI adapter drops that vendor
/// extension, so NetCoreAI cannot run a multi-turn tool conversation against Gemini.
/// <para>
/// This asserts the limitation still exists rather than working around it quietly. When Google stops
/// requiring the signature, or Microsoft starts round-tripping it, this test fails — which is the point:
/// a workaround that outlives its reason is worse than the bug, because nobody knows it can go.
/// </para>
/// </remarks>
public sealed class GeminiToolLimitationTests
{
    [Fact]
    public async Task Gemini_still_refuses_a_follow_up_turn_without_a_thought_signature()
    {
        var provider = LiveProvider.Available.FirstOrDefault(p => p.Name == "Gemini");
        Assert.SkipUnless(provider is not null, "Set GEMINI_FREE_KEY to check whether this limitation still holds.");

        using var client = new HttpClient { BaseAddress = new Uri(provider!.BaseUrl.TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);

        // Exactly what a tool loop sends on its second turn: the call the model made, and its result.
        var response = await client.PostAsJsonAsync(
            "chat/completions",
            new
            {
                model = provider.Model,
                temperature = 0,
                messages = new object[]
                {
                    new { role = "user", content = "Status of order A-7?" },
                    new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new { id = "call_1", type = "function", function = new { name = "get_order", arguments = """{"id":"A-7"}""" } },
                        },
                    },
                    new { role = "tool", tool_call_id = "call_1", content = """{"id":"A-7","status":"shipped"}""" },
                },
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name = "get_order",
                            description = "Look up an order.",
                            parameters = new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } },
                        },
                    },
                },
            },
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        if (body.Contains("quota", StringComparison.OrdinalIgnoreCase) || body.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal))
        {
            Assert.Skip("Gemini rate-limited this check.");
        }

        Assert.False(
            response.IsSuccessStatusCode,
            "Gemini now accepts a follow-up turn without a thought_signature. Remove LiveProvider.MultiTurnTools = false for Gemini, let the tool acceptance run against it, and delete this test.");

        Assert.Contains("thought_signature", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gemini_is_still_usable_for_everything_that_is_not_a_tool_follow_up()
    {
        var provider = LiveProvider.Available.FirstOrDefault(p => p.Name == "Gemini");
        Assert.SkipUnless(provider is not null, "Set GEMINI_FREE_KEY to run this.");

        using var client = new HttpClient { BaseAddress = new Uri(provider!.BaseUrl.TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);

        var response = await client.PostAsJsonAsync(
            "chat/completions",
            new { model = provider.Model, temperature = 0, messages = new[] { new { role = "user", content = "Reply with the single word: ready" } } },
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (body.Contains("quota", StringComparison.OrdinalIgnoreCase) || body.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal))
        {
            Assert.Skip("Gemini rate-limited this check.");
        }

        // The limitation is narrow, and saying so matters: "Gemini does not work" would be wrong, and
        // would cost somebody a provider they could have used.
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("ready", JsonDocument.Parse(body).RootElement
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }
}
