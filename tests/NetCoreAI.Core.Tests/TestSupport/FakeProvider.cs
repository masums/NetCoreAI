using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace NetCoreAI.Core.Tests.TestSupport;

/// <summary>In-memory provider that echoes prompts; configurable to fail, to test fallbacks and lifecycle.</summary>
public sealed class FakeProvider(string id = "fake", ProviderKind kind = ProviderKind.Local, params ModelFormat[] formats) : IModelProvider
{
    public string Id { get; } = id;
    public string DisplayName => $"Fake {Id}";
    public ProviderKind Kind { get; } = kind;
    public IReadOnlyList<ModelFormat> SupportedFormats { get; } = formats.Length == 0 ? [ModelFormat.Gguf] : formats;
    public long MemoryPerLoad { get; set; } = 100;
    public bool CanLoadResult { get; set; } = true;
    public Func<string, string> Reply { get; set; } = prompt => $"{id}: {prompt}";
    public Exception? FailWith { get; set; }
    public int Loads { get; private set; }
    public int Unloads { get; private set; }
    public List<string> Calls { get; } = [];

    public bool CanLoad(ModelDescriptor model) => CanLoadResult && SupportedFormats.Contains(model.Format);

    public ModelCapabilities GetCapabilities(ModelDescriptor model) => new(ModelCapability.Chat | ModelCapability.Embeddings | ModelCapability.Streaming, 2048, 3);

    public ValueTask<MemoryEstimate> EstimateMemoryAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new MemoryEstimate(MemoryPerLoad, 0, FitVerdict.Fits));

    public ValueTask<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default)
    {
        Loads++;
        return ValueTask.FromResult(new LoadedModel(model, Id, null, MemoryPerLoad, options));
    }

    public ValueTask UnloadAsync(LoadedModel model, CancellationToken cancellationToken = default)
    {
        Unloads++;
        return ValueTask.CompletedTask;
    }

    public IChatClient CreateChatClient(LoadedModel model) => new EchoClient(this, model);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model) => new EchoEmbedder();

    private sealed class EchoClient(FakeProvider owner, LoadedModel model) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = messages.Last().Text;
            owner.Calls.Add(prompt);
            if (owner.FailWith is not null)
            {
                throw owner.FailWith;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, owner.Reply(prompt))) { ModelId = options?.ModelId ?? model.Descriptor.Id, Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 5 } });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var word in response.Text.Split(' '))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            }

            yield return new ChatResponseUpdate { Contents = [new UsageContent(response.Usage!)] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class EchoEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(new float[] { v.Length, 1, 0 }))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
