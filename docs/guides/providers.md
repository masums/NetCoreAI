# Providers

Every model, local or remote, is served through `IModelProvider` and surfaces as `IChatClient` or `IEmbeddingGenerator`. Consumer code never references a provider SDK.

## Registered by package

| Package | Provider id | Kind | Notes |
|---|---|---|---|
| `NetCoreAI.Backend.Ollama` | `ollama` | Remote | Local or LAN Ollama. Lists models from the tags endpoint; capabilities (tools, vision, embeddings) read from the show endpoint. |
| `NetCoreAI.Backend.OpenAICompatible` | `openai` | Remote | Presets: OpenAI, Azure OpenAI, vLLM, LM Studio, Groq, DeepSeek, OpenRouter, Together, Mistral, custom. |
| `NetCoreAI.Backend.Anthropic` | `anthropic` | Remote | Claude through the official SDK. No embeddings: pair with another model for the `embed` alias. |
| `NetCoreAI.Backend.Gguf` | `gguf` | Local | LLamaSharp. In progress (WP1.7). |
| `NetCoreAI.Backend.Onnx` | `onnx` | Local | ONNX Runtime GenAI. In progress (WP1.8). |

## Connections

A connection is a named, credentialed configuration of a remote provider: base URL, secret, per-connection timeout, retries, concurrency, rate limit and token pricing. Several connections of the same type can coexist, such as OpenAI-prod, Groq-dev and an office Ollama box. Testing a connection reports auth, reachability and the model list. Syncing registers those models.

Secrets are encrypted with Data Protection, never returned by the API, and overridable per connection with an environment variable.

Custom headers go in connection settings with a `header:` prefix, for example `header:x-org-id`. Azure OpenAI additionally takes an `api-version` setting.

## Selection order

1. The provider named in the model descriptor, when set.
2. Otherwise the first registered provider that supports the format, is enabled, and accepts the model.

Disabling remote providers globally removes them from selectors and makes execution throw `RemoteProvidersDisabledException`. Individual providers can be disabled by id.

## Writing a provider

Implement `IModelProvider`, plus `IConnectionAwareProvider` for remote services or derive from `RemoteModelProviderBase`. Then add an `Add{Name}Backend()` extension on `NetCoreAIBuilder`. Reference `NetCoreAI.Conformance` and derive from the provider suite to prove the contract. No change to Core or the Dashboard is needed.
