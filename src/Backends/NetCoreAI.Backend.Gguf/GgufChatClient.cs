using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Backends.Gguf;

/// <summary>
/// Chat over a loaded GGUF model. Applies the model's chat template (or an override), maps
/// <see cref="ChatOptions"/> onto llama.cpp sampling, constrains output with a GBNF grammar when a
/// response format is requested, and reports token usage.
/// </summary>
internal sealed class GgufChatClient(GgufLoadedModel model, ILogger logger) : IChatClient
{
    private readonly ChatClientMetadata _metadata = new("gguf", null, model.Descriptor.Id);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        UsageDetails? usage = null;

        // Reuse the streaming path so both entry points share one prompt build and one generation.
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent t:
                        text.Append(t.Text);
                        break;
                    case UsageContent u:
                        usage = u.Details;
                        break;
                }
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()))
        {
            ModelId = model.Descriptor.Id,
            FinishReason = ChatFinishReason.Stop,
            Usage = usage,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var prompt = BuildPrompt(messages, options);
        var inference = BuildInferenceParams(options);
        var stopwatch = Stopwatch.StartNew();
        var output = new StringBuilder();

        // StatelessExecutor evaluates the whole prompt each call, which is what we want: the session
        // history is owned by NetCoreAI. Executors are pooled because each one allocates a context.
        var executor = model.RentExecutor(logger);

        IAsyncEnumerator<string>? tokens = null;
        try
        {
            tokens = executor.InferAsync(prompt, inference, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                string token;
                try
                {
                    if (!await tokens.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    token = tokens.Current;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (token.Length == 0)
                {
                    continue;
                }

                output.Append(token);
                yield return new ChatResponseUpdate(ChatRole.Assistant, token) { ModelId = model.Descriptor.Id };
            }
        }
        finally
        {
            if (tokens is not null)
            {
                await tokens.DisposeAsync().ConfigureAwait(false);
            }

            (inference.SamplingPipeline as IDisposable)?.Dispose();
            model.ReturnExecutor(executor);
        }

        stopwatch.Stop();
        var usage = CountUsage(prompt, output.ToString());
        logger.LogDebug(
            "GGUF generation for {ModelId}: {OutputTokens} tokens in {ElapsedMs} ms ({TokensPerSecond:F1} tok/s)",
            model.Descriptor.Id, usage.OutputTokenCount, stopwatch.ElapsedMilliseconds,
            usage.OutputTokenCount / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));

        yield return new ChatResponseUpdate { ModelId = model.Descriptor.Id, Contents = [new UsageContent(usage)] };
    }

    /// <summary>Renders the conversation with the model's chat template, or a plain fallback when it has none.</summary>
    internal string BuildPrompt(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages.ToList();
        var template = model.CreateTemplate();
        if (template is null)
        {
            return FallbackPrompt(list);
        }

        try
        {
            foreach (var message in list)
            {
                var text = string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));
                if (!string.IsNullOrEmpty(text))
                {
                    template.Add(RoleName(message.Role), text);
                }
            }

            template.AddAssistant = true;
            return Encoding.UTF8.GetString(template.Apply());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A malformed or unsupported Jinja template must not take the request down.
            logger.LogWarning(ex, "Chat template for {ModelId} could not be applied; falling back to a plain transcript.", model.Descriptor.Id);
            return FallbackPrompt(list);
        }
    }

    private static string RoleName(ChatRole role) =>
        role == ChatRole.System ? "system" : role == ChatRole.Assistant ? "assistant" : role == ChatRole.Tool ? "tool" : "user";

    private static string FallbackPrompt(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            var text = string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));
            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(RoleName(message.Role)).Append(": ").AppendLine(text);
            }
        }

        return sb.Append("assistant: ").ToString();
    }

    private InferenceParams BuildInferenceParams(ChatOptions? options)
    {
        var defaults = model.Descriptor.DefaultParameters;
        var pipeline = new DefaultSamplingPipeline
        {
            // Grammar is init-only, so structured output has to be decided before the pipeline exists.
            Grammar = BuildGrammar(options?.ResponseFormat) is { } gbnf ? new Grammar(gbnf, "root") : null,
            Temperature = options?.Temperature ?? defaults.Temperature ?? 0.7f,
            TopP = options?.TopP ?? defaults.TopP ?? 0.95f,
            TopK = options?.TopK ?? defaults.TopK ?? 40,
            RepeatPenalty = defaults.RepeatPenalty ?? 1.0f,
            FrequencyPenalty = options?.FrequencyPenalty ?? defaults.FrequencyPenalty ?? 0f,
            PresencePenalty = options?.PresencePenalty ?? defaults.PresencePenalty ?? 0f,
        };

        var seed = options?.Seed ?? defaults.Seed;
        if (seed is not null)
        {
            pipeline.Seed = unchecked((uint)seed.Value);
        }

        IReadOnlyList<string> stops = options?.StopSequences?.ToList() ?? defaults.StopSequences ?? [];
        return new InferenceParams
        {
            MaxTokens = options?.MaxOutputTokens ?? defaults.MaxOutputTokens ?? -1,
            SamplingPipeline = pipeline,
            AntiPrompts = stops,
        };
    }

    /// <summary>Turns a response format into a GBNF grammar; null when the caller wants free text.</summary>
    internal static string? BuildGrammar(ChatResponseFormat? format) => format switch
    {
        ChatResponseFormatJson { Schema: { } schema } => JsonSchemaToGbnf.Convert(schema),
        ChatResponseFormatJson => JsonSchemaToGbnf.AnyJson,
        _ => null,
    };

    /// <summary>Token counts from the model's own tokenizer, so the playground shows real numbers.</summary>
    private UsageDetails CountUsage(string prompt, string output)
    {
        try
        {
            long input = model.Weights.Tokenize(prompt, true, true, Encoding.UTF8).Length;
            long generated = output.Length == 0 ? 0 : model.Weights.Tokenize(output, false, false, Encoding.UTF8).Length;
            return new UsageDetails { InputTokenCount = input, OutputTokenCount = generated, TotalTokenCount = input + generated };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Token counting failed for {ModelId}.", model.Descriptor.Id);
            return new UsageDetails();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? _metadata
            : serviceType == typeof(LLamaWeights) ? model.Weights
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <summary>The model handle is owned by the lifecycle manager, so disposing a client releases nothing.</summary>
    public void Dispose()
    {
    }
}
