# Phase 1 — Foundation

**Goal:** an existing ASP.NET Core app adds two lines and gets local + remote model management, a chat playground and a dashboard, with all model access through `IChatClient`.
**Exit criterion:** G1 (`dotnet add package` → first local chat ≤ 15 min) and G2 (zero mandatory external dependencies) demonstrated on Windows + Linux.

## Architecture for this phase

```
Host Program.cs --AddNetCoreAI()--> NetCoreAIBuilder --AddXxxBackend()--> IModelProvider registrations
                --MapNetCoreAI()--> RouteGroup "/netcoreai" [authz policy]
                                      |-- Razor Components (dashboard)        ADR-0002
                                      |-- /api/*  management API (minimal API)
                                      |-- /health
                                      +-- /_blazor  (interactive circuit hub)

Core services
  ModelRegistry          IMetadataStore (EF Core, SQLite default)      ADR-0003
  ProviderRegistry       picks IModelProvider by format + hardware + preference
  ModelLifecycleManager  IHostedService: load/unload/warm-up/idle-unload, memory budget, concurrency gates
  ChatClientFactory      alias -> loaded model -> IChatClient (wrapped in middleware pipeline)
  HardwareProbe          OS/CPU/RAM/GPU/NPU + FitEstimator
  HubService             Hugging Face API client + curated manifest
  DownloadManager        resumable, verified, SignalR-progress, persisted
  ConnectionManager      remote provider connections, secrets via Data Protection
  ChatSessionService     sessions/messages persistence
```

### Key public types (Abstractions)
- `IModelProvider`: `Id`, `DisplayName`, `Kind` (Local/Remote), `SupportedFormats`, `CanLoad(ModelDescriptor)`, `LoadAsync(ModelDescriptor, LoadOptions, ct)`, `UnloadAsync(LoadedModel, ct)`, `CreateChatClient(LoadedModel)`, `CreateEmbeddingGenerator(LoadedModel)`, `GetCapabilities(ModelDescriptor)`, `EstimateMemoryAsync(ModelDescriptor, LoadOptions, ct)`.
- `ModelDescriptor` (record: Id, Name, Alias, Format, ProviderId, ConnectionId, Path, SizeBytes, Quantization, ContextLength, Family, ChatTemplate, DefaultParams, Source, Revision, Sha256, Tags), `ModelCapabilities` (flags: Chat, Embeddings, ToolCalling, Vision, JsonMode, StructuredOutput; `MaxContext`), `ModelStatus`, `LoadedModel` (handle + descriptor + provider id + loaded-at + memory used), `LoadOptions` (context size, GPU layers, batch, flash attention, KV cache type, threads, execution provider), `MemoryEstimate` + `FitVerdict` (Fits/Tight/WontFit).
- `IModelRegistry`, `IChatClientFactory` (`Get(alias)`, `GetEmbeddingGenerator(alias)`, `TryGet`), `IHardwareProbe` + `HardwareInfo`, `IModelSource` (Hub extensibility), `IDownloadManager`, `IProviderConnectionStore`, `ISecretProtector`, `IMetadataStore`, `NetCoreAIOptions`, `NetCoreAIBuilder`.
- Remote providers are `IModelProvider` implementations whose `ModelDescriptor.ConnectionId` points at a `ProviderConnection`; "load" is cheap (client creation) and never counts against the memory budget.

### Middleware pipeline (applied by `ChatClientFactory` in this order)
`UseLogging` → `UseOpenTelemetry` (GenAI semantic conventions) → optional `UseDistributedCache` → rate limiting (per model / per connection) → `UseFunctionInvocation` → provider client. Embedding generators get the same minus function invocation.

## Work packages (in order; each ends with tests and a checked TASKS.md line)

### WP1.1 Abstractions + Core skeleton (Abstractions & Core group)
Interfaces and records above; `NetCoreAIOptions` (`DataDirectory`, `Dashboard.Path`, `Dashboard.Authorization`, `Models.IdleUnloadTimeout`, `Models.MemoryBudget`, `Models.DefaultConcurrency`, `Network.OfflineMode`, `Network.Proxy`, `Network.HuggingFaceEndpoint`, `Providers.RemoteEnabled`, `Telemetry.Enabled`); `AddNetCoreAI()` returning `NetCoreAIBuilder` (idempotent, binds `appsettings:NetCoreAI`, adds Data Protection, Razor Components, health checks); `ProviderRegistry`; `ChatClientFactory` with middleware; in-memory `IMetadataStore` for tests.
Tests: options binding, provider selection order (preference > hardware > format), alias resolution, pipeline order.

### WP1.2 Storage.Sqlite + data model
EF Core model for the §9 tables needed now (Models, Downloads, ProviderConnections, Sessions, Messages, Settings, plus `Instances` for the ADR-0003 multi-instance warning). `SqliteMetadataStore : IMetadataStore`; migrations; `MetadataStoreConformanceTests` in Conformance run against SQLite and the in-memory store. `AddNetCoreAI()` auto-registers SQLite when no other store was registered (`TryAdd`).

### WP1.3 Dashboard shell + `MapNetCoreAI()` (Dashboard Shell, Security group)
Endpoint group with authorization (default deny → clear 403 page telling the admin what policy to set), Razor Components root with layout, nav, theme, `/health` (models ready, disk space, store reachable), `/api/settings` GET/PUT, roles `NetCoreAI.Admin/Builder/User` as policies (`RequireRole` or claim mapping via `Dashboard.RoleClaimType`). Overview page with live counters (SSR + polling; upgraded to circuit-driven in WP1.9). Structured logging categories `NetCoreAI.*`; `ActivitySource("NetCoreAI")` + `Meter("NetCoreAI")`.
Integration test: `WebApplicationFactory` host with and without auth proves default-deny and that no route outside `/netcoreai` changed.

### WP1.4 Remote providers (Remote Providers group)
Done before local backends: they need no model files, so the whole registry → factory → playground path is verifiable in CI.
- `ProviderConnection` entity + `ConnectionManager` (CRUD, `TestAsync` → auth/reachability/model list, health status, secrets via `IDataProtector` purpose `NetCoreAI.Secrets`, env-var override `NETCOREAI__CONNECTIONS__{name}__APIKEY`).
- `Backend.OpenAICompatible`: `OpenAI` SDK + `Microsoft.Extensions.AI.OpenAI`; presets table (OpenAI, Azure OpenAI with deployment + api-version, vLLM, LM Studio, Groq, DeepSeek, OpenRouter, Together, Mistral, Custom) with default base URL, model-list support flag and curated model list; embeddings via `/embeddings`.
- `Backend.Anthropic`: official `Anthropic` SDK `AsIChatClient()`; configurable base URL; capabilities Chat + ToolCalling + Vision, no embeddings (UI must pair with another embedding model).
- `Backend.Ollama`: `OllamaSharp` (`OllamaApiClient` is an `IChatClient` and `IEmbeddingGenerator`); model list from `/api/tags`; pull-model support surfaced to the Hub later.
- Routing and fallback: `FallbackChatClient` (ordered list, failover on provider error / timeout / 429 / not-loaded-and-no-memory) used by aliases with `Fallbacks`.
- `/api/providers/connections` endpoints + dashboard pages (list, add/edit with preset picker, test button, health badge). "Disable remote providers" setting removes them from selectors and makes `ChatClientFactory` throw `RemoteProvidersDisabledException`.
Conformance: `ModelProviderConformanceTests` run against a local fake OpenAI-compatible server (in-test Kestrel) so the suite is green without secrets; real-key tests gated by `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `OLLAMA_HOST`.

### WP1.5 Hardware probe + fit estimator (Hardware & Lifecycle group)
`HardwareProbe`: OS/arch, logical cores, total/available RAM, GPUs (NVIDIA via `nvidia-smi`/NVML with graceful absence; Windows via DXGI enumeration; Linux via `/sys/class/drm` + Vulkan if present; macOS Metal via `system_profiler`), NPU presence flag. `FitEstimator`: bytes = params × bits/8 × 1.1 + KV cache(ctx, layers, kv type) → `FitVerdict` for CPU-only, full-GPU and split; recommends quantization + `GpuLayers`. `GET /api/hardware`; dashboard hardware card.

### WP1.6 Model lifecycle manager
`ModelLifecycleManager : IHostedService`: load/unload with per-model concurrency policy (SingleSlot default / Pool(n) / RejectWhenBusy), idle-unload timer, memory budget accounting (refuse load with `ModelWontFitException`, never crash host), warm-up on startup for models flagged `LoadOnStartup`, readiness gate feeding `/health`, graceful shutdown cancels in-flight generations then releases native handles. Registry status transitions Available → Loading → Loaded → Error.

### WP1.7 GGUF provider (`Backend.Gguf`) (Model Providers, local) — done
LLamaSharp `LLamaWeights` + `LLamaContext` per slot; `LLamaSharp.Backend.Cpu` referenced by the base package; `NetCoreAI.Backend.Gguf.Cuda12/Vulkan/Metal` are thin packages that only add the native backend package. GGUF metadata reader (own streaming header parser) for architecture, quantization, context length, chat template, embedding flag, used by Import and Hub without loading the model. Chat template applied from metadata via LLamaSharp template support, override per model. GBNF grammar from JSON schema for structured output. Embedding models through `LLamaEmbedder`. Options: context size, GPU layers, batch, flash attention, KV cache type, threads.
Tests: metadata parser unit tests on hand-built headers; conformance against a < 500 MB fixture (Qwen2.5-0.5B-Instruct Q4_K_M) downloaded on demand, `Category=Model`.

### WP1.8 ONNX provider (`Backend.Onnx`) — done
`Microsoft.ML.OnnxRuntimeGenAI` `Model`/`Tokenizer`/`Generator`; loads HF folder with `genai_config.json`; execution provider preference from settings (CPU/DirectML/CUDA); streaming token generation; embeddings via plain `Microsoft.ML.OnnxRuntime` session with mean pooling for sentence-transformers exports (tokenizer via `Microsoft.ML.Tokenizers`). Fixture: `onnx-community/Qwen2.5-0.5B-Instruct` int4 CPU.

### WP1.9 Model Hub (Model Hub group) — done

**Deviation from this plan:** download progress is pushed over Server-Sent Events (`GET /api/downloads/events`) rather than a SignalR hub. The dashboard is plain JS with no build step, and SignalR would mean shipping its client library — a CDN reference that breaks air-gapped installs, or ~40 KB embedded in the package. SSE needs no client library, is the transport the chat playground already streams over, and reconnects on its own. Revisit if the dashboard ever needs bidirectional messaging.
`HuggingFaceClient` (search with filters, model info, file list with LFS sha256/size, README raw, gated flag, token header), `CuratedManifest` (JSON fetched from the repo `manifest/recommended.json` with embedded fallback copy), fit badge per variant, Markdig README render. `DownloadManager`: queue with priority, range requests, N parallel chunks per file, token-bucket bandwidth limit, `.part` files + JSON sidecar so it resumes after restart, SHA-256 verify vs LFS metadata, progress → SignalR hub `/netcoreai/_hubs/downloads` + persisted `Downloads` rows; pause/resume/cancel. Import: local path, chunked upload, URL; auto-detect via GGUF reader / `genai_config.json`. Endpoints `/api/hub/*`, `/api/downloads/*`, `/api/models/import`. Dashboard pages: Hub search, Recommended, model detail, downloads panel.

### WP1.10 Model registry + storage management (Local Model Management group) — done
Registry page (status, size, quantization, context, capability icons, last used, load/unload, set default chat/embed, aliases editor, per-model default params, tags/notes); `chatClientFactory.Get("alias")`; storage page (disk usage per model, delete with confirm, orphan scan, quota warning threshold). Endpoints `/api/models/*`.

### WP1.11 Chat playground (Chat Playground group)
Interactive-server page: model picker (auto-load if fits), streaming, Markdown + code blocks with copy button, regenerate / edit-and-resend / stop, parameter side panel bound to `ChatOptions`, system prompt, session list (persist, rename, delete, export JSON/Markdown), per-message tokens/latency/cost (cost from connection `CostPer1KTokens`). Public `POST /api/chat` (SSE) + `/api/sessions` CRUD for non-dashboard clients.

### WP1.12 Settings, ops polish, acceptance (Settings/Ops + Phase 1 acceptance) — settings, meters and cost done; the timed onboarding run on clean VMs is outstanding
Settings pages (general, network/proxy/offline, providers enable/disable + execution provider preference); overview page fed by `Meter` counters; OTel traces for every generation (`gen_ai.*` attributes); health check details. Then the acceptance list in TASKS.md, including the timed onboarding run on clean Windows + Linux VMs, documented in `docs/guides/getting-started.md`.

## Risks and mitigations
| Risk | Mitigation |
|---|---|
| Native backend package size / RID selection | Base GGUF package pulls CPU only; GPU variants are separate packages; document `RuntimeIdentifier` guidance |
| LLamaSharp / ORT GenAI API churn | Provider code isolated behind `IModelProvider`; conformance suite catches regressions on upgrade |
| Host already uses Blazor | ADR-0002: dedicated hub path, render-mode isolation, documented |
| HF rate limits (open question 6) | Server-side calls with optional token, response cache in metadata store, offline fallback |
| Memory estimates wrong → OOM | Conservative 10 % headroom, refuse-to-load path tested with a tiny budget |

## Definition of done
- All Phase 1 boxes in TASKS.md checked; conformance suite green for 5 providers (3 remote via fake servers in CI, 2 local via fixtures in the nightly job).
- `docs/guides/getting-started.md`, `hosting.md`, `providers.md` written.
- Version `0.1.0-alpha.1` packed and the `MinimalApi` sample chats with a downloaded GGUF model.
