using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace NetCoreAI.Backends.Onnx;

/// <summary>
/// Chat over a model loaded by ONNX Runtime GenAI. Renders the conversation with the chat template the
/// model folder ships, maps <see cref="ChatOptions"/> onto GenAI search options, streams tokens off the
/// request thread and reports token usage.
/// </summary>
internal sealed class OnnxChatClient(OnnxGenerativeModel model, ILogger logger) : IChatClient
{
    private readonly ChatClientMetadata _metadata = new("onnx", null, model.Descriptor.Id);

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
        var stopwatch = Stopwatch.StartNew();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var counts = new GenerationCounts();

        // Token generation is a synchronous native loop, so it runs on a worker thread and the tokens
        // reach the caller through a channel; the request thread never blocks on the model.
        var generation = Task.Run(() => Generate(prompt, options, channel.Writer, counts, cancellationToken), CancellationToken.None);

        await foreach (var token in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, token) { ModelId = model.Descriptor.Id };
        }

        // Surfaces any generation failure, and the OperationCanceledException when the caller stopped it.
        await generation.ConfigureAwait(false);

        stopwatch.Stop();
        var usage = new UsageDetails
        {
            InputTokenCount = counts.PromptTokens,
            OutputTokenCount = counts.GeneratedTokens,
            TotalTokenCount = counts.PromptTokens + counts.GeneratedTokens,
        };

        logger.LogDebug(
            "ONNX generation for {ModelId}: {OutputTokens} tokens in {ElapsedMs} ms ({TokensPerSecond:F1} tok/s)",
            model.Descriptor.Id, counts.GeneratedTokens, stopwatch.ElapsedMilliseconds,
            counts.GeneratedTokens / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));

        yield return new ChatResponseUpdate { ModelId = model.Descriptor.Id, Contents = [new UsageContent(usage)] };
    }

    /// <summary>Runs the native generation loop, writing decoded tokens to the channel until the model stops.</summary>
    private void Generate(string prompt, ChatOptions? options, ChannelWriter<string> writer, GenerationCounts counts, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            using var sequences = model.Tokenizer.Encode(prompt);
            var promptTokens = sequences[0].Length;
            counts.PromptTokens = promptTokens;

            using var parameters = BuildParameters(options, promptTokens);
            using var generator = new Generator(model.Model, parameters);
            generator.AppendTokenSequences(sequences);

            using var stream = model.Tokenizer.CreateStream();
            var stops = options?.StopSequences?.ToList() ?? model.Descriptor.DefaultParameters.StopSequences?.ToList() ?? [];
            var tail = new StringBuilder();

            while (!generator.IsDone())
            {
                cancellationToken.ThrowIfCancellationRequested();
                generator.GenerateNextToken();

                var sequence = generator.GetSequence(0);
                if (sequence.Length <= promptTokens + counts.GeneratedTokens)
                {
                    // No new token was appended (the model finished on this step).
                    break;
                }

                counts.GeneratedTokens = sequence.Length - promptTokens;
                var text = stream.Decode(sequence[sequence.Length - 1]);
                if (text.Length == 0)
                {
                    continue;
                }

                if (stops.Count > 0)
                {
                    tail.Append(text);
                    if (FindStop(tail, stops) is { } cut)
                    {
                        if (cut > 0)
                        {
                            writer.TryWrite(tail.ToString(0, cut));
                        }

                        break;
                    }

                    writer.TryWrite(text);
                    continue;
                }

                writer.TryWrite(text);
            }
        }
        catch (Exception ex)
        {
            failure = ex is OnnxRuntimeGenAIException
                ? new NetCoreAIException($"ONNX Runtime could not generate with '{model.Descriptor.Name}': {ex.Message}", ex)
                : ex;
        }
        finally
        {
            writer.TryComplete();
        }

        if (failure is not null)
        {
            // Rethrown to the caller when it awaits the generation task, after the channel has drained.
            throw failure;
        }
    }

    /// <summary>Index at which generated text should be cut when a stop sequence has appeared; null while none has.</summary>
    private static int? FindStop(StringBuilder tail, List<string> stops)
    {
        var text = tail.ToString();
        var cut = -1;
        foreach (var stop in stops)
        {
            if (stop.Length == 0)
            {
                continue;
            }

            var index = text.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0 && (cut < 0 || index < cut))
            {
                cut = index;
            }
        }

        return cut < 0 ? null : cut;
    }

    /// <summary>Maps chat options and the model's own defaults onto GenAI search options.</summary>
    private GeneratorParams BuildParameters(ChatOptions? options, int promptTokens)
    {
        var defaults = model.Descriptor.DefaultParameters;
        var search = model.Folder.SearchDefaults;
        var parameters = new GeneratorParams(model.Model);

        var temperature = options?.Temperature ?? defaults.Temperature ?? search.Temperature;
        var topP = options?.TopP ?? defaults.TopP ?? search.TopP;
        var topK = options?.TopK ?? defaults.TopK ?? search.TopK;
        var repeat = defaults.RepeatPenalty ?? search.RepetitionPenalty;

        // max_length counts the prompt too, so the budget is prompt + requested output, capped by the context.
        var maxOutput = options?.MaxOutputTokens ?? defaults.MaxOutputTokens ?? 512;
        var maxLength = Math.Min(model.ContextSize, promptTokens + Math.Max(1, maxOutput));
        TrySet(parameters, "max_length", maxLength);

        if (temperature is { } t)
        {
            TrySet(parameters, "temperature", t);
            TrySet(parameters, "do_sample", t > 0);
        }

        if (topP is { } p)
        {
            TrySet(parameters, "top_p", p);
        }

        if (topK is { } k and > 0)
        {
            TrySet(parameters, "top_k", k);
        }

        if (repeat is { } r and > 0)
        {
            TrySet(parameters, "repetition_penalty", r);
        }

        if ((options?.Seed ?? defaults.Seed) is { } seed)
        {
            TrySet(parameters, "random_seed", (double)unchecked((int)seed));
        }

        ApplyGuidance(parameters, options?.ResponseFormat);
        return parameters;
    }

    /// <summary>
    /// Constrains output to a JSON schema when the runtime was built with guidance support. Support is a
    /// build-time choice, so failure is logged rather than thrown and the model answers unconstrained.
    /// </summary>
    private void ApplyGuidance(GeneratorParams parameters, ChatResponseFormat? format)
    {
        var (type, data) = format switch
        {
            ChatResponseFormatJson { Schema: { } schema } => ("json_schema", schema.GetRawText()),
            ChatResponseFormatJson => ("json_schema", JsonSerializer.Serialize(new { type = "object" })),
            _ => (null, null),
        };

        if (type is null || data is null)
        {
            return;
        }

        try
        {
            parameters.SetGuidance(type, data, enableFFTokens: false);
        }
        catch (Exception ex) when (ex is OnnxRuntimeGenAIException or EntryPointNotFoundException or DllNotFoundException)
        {
            logger.LogWarning(
                ex,
                "This build of ONNX Runtime GenAI has no guidance support, so the JSON schema for {ModelId} is only a prompt hint. Use a GGUF model for guaranteed structured output.",
                model.Descriptor.Id);
        }
    }

    private void TrySet(GeneratorParams parameters, string name, double value)
    {
        try
        {
            parameters.SetSearchOption(name, value);
        }
        catch (Exception ex) when (ex is OnnxRuntimeGenAIException)
        {
            logger.LogDebug(ex, "Search option {Option} is not supported by {ModelId}.", name, model.Descriptor.Id);
        }
    }

    private void TrySet(GeneratorParams parameters, string name, bool value)
    {
        try
        {
            parameters.SetSearchOption(name, value);
        }
        catch (Exception ex) when (ex is OnnxRuntimeGenAIException)
        {
            logger.LogDebug(ex, "Search option {Option} is not supported by {ModelId}.", name, model.Descriptor.Id);
        }
    }

    /// <summary>Renders the conversation with the model's chat template, or a plain transcript when it has none.</summary>
    internal string BuildPrompt(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages.ToList();
        try
        {
            var payload = JsonSerializer.Serialize(list.Select(m => new TemplateMessage(RoleName(m.Role), Text(m))).Where(m => m.Content.Length > 0));
            // An override on the model wins; otherwise the folder's own template, which the runtime only
            // reads by itself when it lives inside tokenizer_config.json.
            var template = model.Descriptor.ChatTemplate ?? model.Folder.ChatTemplate;
            var rendered = model.Tokenizer.ApplyChatTemplate(template, payload, null, add_generation_prompt: true);
            if (!string.IsNullOrEmpty(rendered))
            {
                return rendered;
            }

            logger.LogDebug("Chat template for {ModelId} rendered nothing; using a plain transcript.", model.Descriptor.Id);
        }
        catch (Exception ex) when (ex is OnnxRuntimeGenAIException or NotSupportedException or JsonException)
        {
            // A model folder without a usable template must not take the request down.
            logger.LogWarning(ex, "Chat template for {ModelId} could not be applied; falling back to a plain transcript.", model.Descriptor.Id);
        }

        return FallbackPrompt(list);
    }

    private static string Text(ChatMessage message) => string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));

    private static string RoleName(ChatRole role) =>
        role == ChatRole.System ? "system" : role == ChatRole.Assistant ? "assistant" : role == ChatRole.Tool ? "tool" : "user";

    private static string FallbackPrompt(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            var text = Text(message);
            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(RoleName(message.Role)).Append(": ").AppendLine(text);
            }
        }

        return sb.Append("assistant: ").ToString();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? _metadata
            : serviceType == typeof(Model) ? model.Model
            : serviceType == typeof(Tokenizer) ? model.Tokenizer
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <summary>The model handle is owned by the lifecycle manager, so disposing a client releases nothing.</summary>
    public void Dispose()
    {
    }

    /// <summary>Token counts filled in by the generation thread and read after it finishes.</summary>
    private sealed class GenerationCounts
    {
        public int PromptTokens { get; set; }

        public int GeneratedTokens { get; set; }
    }

    /// <summary>One message in the JSON array the GenAI chat template consumes.</summary>
    private readonly record struct TemplateMessage(
        [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
        [property: System.Text.Json.Serialization.JsonPropertyName("content")] string Content);
}
