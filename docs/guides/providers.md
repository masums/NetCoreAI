# Providers

Every model, local or remote, is served through `IModelProvider` and surfaces as `IChatClient` or `IEmbeddingGenerator`. Consumer code never references a provider SDK.

## Registered by package

| Package | Provider id | Kind | Notes |
|---|---|---|---|
| `NetCoreAI.Backend.Ollama` | `ollama` | Remote | Local or LAN Ollama. Lists models from the tags endpoint; capabilities (tools, vision, embeddings) read from the show endpoint. |
| `NetCoreAI.Backend.OpenAICompatible` | `openai` | Remote | Presets: OpenAI, Azure OpenAI, Google Gemini, vLLM, LM Studio, Groq, DeepSeek, OpenRouter, Together, Mistral, custom. |
| `NetCoreAI.Backend.Anthropic` | `anthropic` | Remote | Claude through the official SDK. No embeddings: pair with another model for the `embed` alias. |
| `NetCoreAI.Backend.Gguf` | `gguf` | Local | llama.cpp through LLamaSharp. Reads the GGUF header for capabilities and memory estimates, applies the file's chat template, constrains JSON output with a GBNF grammar. Needs `LLamaSharp.Backend.Cpu` (or `.Cuda12` / `.Vulkan`) referenced from your own project. |
| `NetCoreAI.Backend.Onnx` | `onnx` | Local | ONNX Runtime GenAI for generative models, ONNX Runtime for sentence-transformers embedding exports. CPU included; add the ORT GenAI CUDA or DirectML package for GPU. |

## Connections

A connection is a named, credentialed configuration of a remote provider: base URL, secret, per-connection timeout, retries, concurrency, rate limit and token pricing. Several connections of the same type can coexist, such as OpenAI-prod, Groq-dev and an office Ollama box. Testing a connection reports auth, reachability and the model list. Syncing registers those models.

Secrets are encrypted with Data Protection, never returned by the API, and overridable per connection with an environment variable.

Custom headers go in connection settings with a `header:` prefix, for example `header:x-org-id`. Azure OpenAI additionally takes an `api-version` setting.

## Selection order

1. The provider named in the model descriptor, when set.
2. Otherwise the first registered provider that supports the format, is enabled, and accepts the model.

Disabling remote providers globally removes them from selectors and makes execution throw `RemoteProvidersDisabledException`. Individual providers can be disabled by id.

## Running GGUF models locally

```csharp
builder.Services.AddNetCoreAI().AddGgufBackend();
```

Point a model at a `.gguf` file and the provider reads its header to answer capability and "will it fit" questions without loading weights: architecture, context length, layer and head counts, quantization, embedded chat template and whether the file is an embedding model.

**Load options.** Context size, GPU layer count, batch size, flash attention, KV cache type (f16, q8, q4) and thread count map onto llama.cpp. A requested context larger than the training length is clamped down with a log line.

**Chat template.** Taken from the GGUF header, so Qwen, Llama and Phi models each get their own markers. Set `ChatTemplate` on the model to override it; a model with no template falls back to a plain transcript.

**Structured output.** `ChatResponseFormat.ForJsonSchema` is compiled to a GBNF grammar, so llama.cpp can only sample tokens that keep the output valid. This constrains generation rather than validating afterwards, so there is no retry loop.

### Google Gemini

A preset on the OpenAI-compatible provider rather than a backend of its own, because Gemini publishes a
real OpenAI-shaped API. Base URL `https://generativelanguage.googleapis.com/v1beta/openai`, an API key
from AI Studio, and chat, streaming, model listing and embeddings all work.

Two things are worth knowing before you pick it:

**No tool calling, and NetCoreAI says so rather than letting you find out.** Gemini returns a
`thought_signature` inside each tool call and refuses the following turn without it; the
`Microsoft.Extensions.AI` OpenAI adapter drops that vendor field. A single call works and the turn after
it does not — and an agent loop is multi-turn by definition. So Gemini chat models are registered without
the tool-calling capability, which keeps agents from choosing one and failing on their second step. A
tripwire test fails when Google or Microsoft fixes this, so the restriction cannot outlive its reason.

**Embeddings are 3072-dimension** (`gemini-embedding-001`), not the 1536 that most OpenAI-compatible
services use. A knowledge base is built around its embedding model, so this is not a setting to change
later.

Gemini's listing returns ids as `models/gemini-3.6-flash` while its documentation uses the bare name.
Both work for generation, and NetCoreAI treats them the same.

**The native library is a separate reference, and it goes in your project.** `NetCoreAI.Backend.Gguf`
is the provider; the llama.cpp binaries come from `LLamaSharp.Backend.Cpu`, and that package delivers
them through MSBuild build targets. NuGet does not flow build targets to a consumer of a consumer, so
NetCoreAI cannot pass them on however it depends on them. Reference one native backend package
directly:

```bash
dotnet add package NetCoreAI.Backend.Gguf
dotnet add package LLamaSharp.Backend.Cpu
```

Skip the second line and everything builds, the provider registers, a GGUF file still imports and
reports its architecture and context length — and then the first load fails, because header parsing is
managed code and inference is not. NetCoreAI turns that failure into a message naming the package to
add.

**GPU.** Swap the native package for `LLamaSharp.Backend.Cuda12` (NVIDIA) or
`LLamaSharp.Backend.Vulkan` (AMD, Intel, NVIDIA) and set the execution provider in settings. llama.cpp
picks its backend once per process, so switching needs a host restart.

**Memory.** Each generation borrows a pooled llama.cpp context; contexts are created on demand, reused across requests and released when the model unloads.

## Running ONNX models locally

```csharp
builder.Services.AddNetCoreAI().AddOnnxBackend();
```

Point a model at a folder rather than a file. Two layouts are recognised, and the provider tells them apart by reading the folder:

- **Generative** — an ONNX Runtime GenAI export, recognised by its `genai_config.json`. That file also supplies the context length, layer and head counts, quantization and sampling defaults, so capability and "will it fit" questions are answered without loading the graph.
- **Embedding** — a sentence-transformers export: an `.onnx` graph plus a tokenizer (`vocab.txt` or `tokenizer.json`), with `config.json` giving the dimensions. Pointing at the graph inside an `onnx/` subfolder works too; the reader walks up to the model root.

**Load options.** Context size and thread count apply; GPU layer count, batch size, flash attention and KV cache type do not, because ONNX Runtime neither splits a model across devices nor exposes those knobs. A requested context larger than the export's own is clamped down with a log line.

**Chat template.** Applied from the folder: `tokenizer_config.json` when the template lives there, or a `chat_template.jinja` beside it, which newer Hugging Face exports use and the runtime does not read by itself. Set `ChatTemplate` on the model to override it; a folder with no template falls back to a plain transcript.

**Sampling.** Temperature, top-p, top-k, repetition penalty, seed and max output tokens map onto GenAI search options; `MaxOutputTokens` is added to the prompt length because GenAI counts both against one budget. Stop sequences are enforced by the provider while streaming, since the runtime has none.

**Structured output.** Requested JSON schemas are passed to GenAI guidance, which only some builds of the runtime carry. When it is unavailable the request still succeeds, a warning is logged and the schema is not enforced — so the provider does not advertise `StructuredOutput`, and GGUF remains the choice when valid JSON has to be guaranteed.

**Embeddings.** Token vectors are pooled with the attention mask (mean or CLS, as `1_Pooling/config.json` says) and L2-normalized when the export declares a normalization module, matching what the Python pipeline produces. A model that exposes its own `sentence_embedding` output is used directly.

**GPU.** The base package carries the CPU build. Add `Microsoft.ML.OnnxRuntimeGenAI.Cuda` or `.DirectML` to the host project and set the execution provider in settings. Unlike llama.cpp, the choice is made per load, so it takes effect without a restart; if the native library is missing the load falls back to the CPU with a warning.

**Tool calling.** Not supported: ONNX Runtime GenAI has no native tool-calling protocol, so the provider does not advertise the capability and the UI hides it for these models.

## Writing a provider

Implement `IModelProvider`, plus `IConnectionAwareProvider` for remote services or derive from `RemoteModelProviderBase`. Then add an `Add{Name}Backend()` extension on `NetCoreAIBuilder`. Reference `NetCoreAI.Conformance` and derive from the provider suite to prove the contract. No change to Core or the Dashboard is needed.

## Google Gemini

Gemini serves an OpenAI-compatible endpoint, so it is added as an OpenAI-compatible connection rather than through a package of its own:

```
Base URL   https://generativelanguage.googleapis.com/v1beta/openai
Key        an AI Studio API key
Model      gemini-flash-latest, gemini-2.5-pro, …
```

Chat, streaming and embeddings work. **Tool-calling agents do not**, and the reason is worth knowing before you build one.

Gemini returns a `thought_signature` inside each tool call and requires it to be sent back on the next turn — the request that carries the tool's result. `Microsoft.Extensions.AI`'s OpenAI adapter maps responses onto its own types and drops that vendor extension, so the follow-up is refused:

```
Function call is missing a thought_signature in functionCall parts.
```

The first half works: Gemini decides to call the tool, NetCoreAI runs it. It is continuing the conversation afterwards that fails, and every currently available Gemini model behaves this way. Until it is fixed upstream, use Gemini for chat and retrieval, and another provider for agents with tools.

`GeminiToolLimitationTests` checks this on every run with a key configured, and **fails when the limitation stops being true** — so the workaround is removed when the world changes rather than outliving the problem.
