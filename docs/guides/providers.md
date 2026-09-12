# Providers

Every model, local or remote, is served through `IModelProvider` and surfaces as `IChatClient` or `IEmbeddingGenerator`. Consumer code never references a provider SDK.

## Registered by package

| Package | Provider id | Kind | Notes |
|---|---|---|---|
| `NetCoreAI.Backend.Ollama` | `ollama` | Remote | Local or LAN Ollama. Lists models from the tags endpoint; capabilities (tools, vision, embeddings) read from the show endpoint. |
| `NetCoreAI.Backend.OpenAICompatible` | `openai` | Remote | Presets: OpenAI, Azure OpenAI, vLLM, LM Studio, Groq, DeepSeek, OpenRouter, Together, Mistral, custom. |
| `NetCoreAI.Backend.Anthropic` | `anthropic` | Remote | Claude through the official SDK. No embeddings: pair with another model for the `embed` alias. |
| `NetCoreAI.Backend.Gguf` | `gguf` | Local | llama.cpp through LLamaSharp. Reads the GGUF header for capabilities and memory estimates, applies the file's chat template, constrains JSON output with a GBNF grammar. CPU included; add `.Cuda12` or `.Vulkan` for GPU. |
| `NetCoreAI.Backend.Onnx` | `onnx` | Local | ONNX Runtime GenAI. In progress (WP1.8). |

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

**GPU.** The base package carries the CPU build. Add `NetCoreAI.Backend.Gguf.Cuda12` (NVIDIA) or `NetCoreAI.Backend.Gguf.Vulkan` (AMD, Intel, NVIDIA) and set the execution provider in settings. llama.cpp picks its backend once per process, so switching needs a host restart.

**Memory.** Each generation borrows a pooled llama.cpp context; contexts are created on demand, reused across requests and released when the model unloads.

## Writing a provider

Implement `IModelProvider`, plus `IConnectionAwareProvider` for remote services or derive from `RemoteModelProviderBase`. Then add an `Add{Name}Backend()` extension on `NetCoreAIBuilder`. Reference `NetCoreAI.Conformance` and derive from the provider suite to prove the contract. No change to Core or the Dashboard is needed.
