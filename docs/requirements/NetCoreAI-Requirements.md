# NetCoreAI — Product Requirements & Feature Specification

| | |
|---|---|
| **Product** | NetCoreAI — drop-in AI platform for existing ASP.NET Core applications |
| **Version** | 0.1 (draft) |
| **Date** | 11 September 2026 |
| **Owner** | Masum / Masums |
| **Target runtime** | .NET 10 (LTS), ASP.NET Core 10 |
| **Status** | For review |

---

## 1. Vision

NetCoreAI is a NuGet-distributed framework that turns any existing ASP.NET Core application into an AI-enabled application with **two lines of code** (`AddNetCoreAI()` / `MapNetCoreAI()`) and **no external services**. It ships its own management UI, runs models locally (ONNX, GGUF, Safetensors) and connects to Ollama, OpenAI-compatible and Anthropic providers as peers, lets teams build RAG knowledge bases over their own documents, databases and APIs, exposes existing API endpoints as AI tools, and lets them compose all of that into agents that are callable from their own code and from HTTP.

**One-line positioning:** *"Hangfire for AI" — mount it, open the dashboard, and your app has local models, RAG, tools and agents.*

## 2. Problem Statement

.NET teams that want to add AI to an existing application today face three problems at once: (1) the runtime layer is fragmented across ONNX Runtime GenAI, LLamaSharp, Ollama and cloud SDKs with different APIs; (2) there is no built-in way to find, download, and manage models from inside the application; and (3) RAG, tool-calling and agents require assembling five or six separate libraries plus a vector store, then building admin UI on top. The result is that most teams either ship a thin cloud-API wrapper (data leaves the building, per-token cost) or abandon the feature. Regulated industries, offline deployments and cost-sensitive markets (including Bangladesh and South Asia) need a fully local, self-contained option.

## 3. Goals

| # | Goal | Measure |
|---|---|---|
| G1 | Time from `dotnet add package` to first local chat response ≤ 15 minutes on a clean machine | Onboarding test on Windows and Linux |
| G2 | Zero mandatory external dependencies: no Python, no Docker, no separate server, no cloud account. Remote providers (Ollama, OpenAI-compatible, Anthropic) are optional additions, never requirements | Fresh install with network access only to model downloads |
| G3 | A developer can expose an existing controller endpoint as an agent tool in ≤ 5 minutes without writing new code | Task completion test |
| G4 | An agent designed in the UI is callable from C# (`IAgentClient`) and HTTP with identical behaviour | Contract tests |
| G5 | Runtime backends and model formats are pluggable so new formats can be added without changing consumer code | Provider interface stability across minor versions |

## 4. Non-Goals (v1)

| Non-goal | Rationale |
|---|---|
| Model training or fine-tuning | Separate product; requires GPU pipelines outside the app's scope |
| Being a cloud-only gateway | Cloud and Ollama providers are first-class and can be the only providers in a deployment, but the product's differentiator is that local models work with no external service. Cloud-specific features (fine-tuning, batch APIs, provider billing dashboards) are out of scope |
| Multi-modal generation (image/audio output) | Vision *input* is P2; generation is out of scope until runtimes stabilise on .NET |
| Replacing the host application's authentication | NetCoreAI plugs into the host's existing auth; it never becomes an identity provider |
| A general workflow engine / visual node graph | Agents are declarative (prompt + model + tools + knowledge). Multi-step orchestration is P2 |
| Mobile/MAUI targets | ASP.NET Core only in v1; the core abstractions are portable by design |

## 5. Personas

| Persona | Description | Primary needs |
|---|---|---|
| **App Developer** | Owns an existing ASP.NET Core app; adds NetCoreAI via NuGet | Minimal integration, typed SDK, DI-friendly, no breaking changes to their app |
| **AI Admin** | Non-developer or ops user with access to the NetCoreAI dashboard | Browse and download models, manage storage, build knowledge bases, design agents |
| **End User** | User of the host application | Chat/agent features embedded in the host app; never sees the dashboard |
| **Integration Developer** | Calls agents from other systems | Stable HTTP API, streaming, API keys, OpenAPI spec |

## 6. Solution Architecture (summary)

### 6.1 Package structure

```
NetCoreAI.Abstractions      – interfaces, records, no dependencies beyond Microsoft.Extensions.AI.Abstractions
NetCoreAI.Core              – model registry, hardware probe, downloader, RAG pipeline, tool/agent engine
NetCoreAI.Dashboard         – embedded UI (Razor Components / Blazor Server) + management API, mounted at /netcoreai
NetCoreAI.Backend.Onnx      – ONNX Runtime GenAI provider (CPU / DirectML / CUDA variants)
NetCoreAI.Backend.Gguf      – LLamaSharp provider (CPU / CUDA / Vulkan / Metal variants)
NetCoreAI.Backend.Safetensors – TorchSharp-based provider (see §7.1.4 for constraints)
NetCoreAI.Backend.Ollama    – Ollama provider (local or LAN server)
NetCoreAI.Backend.OpenAICompatible – OpenAI, Azure OpenAI, and any OpenAI-compatible server (vLLM, LM Studio, Groq, DeepSeek, OpenRouter, etc.)
NetCoreAI.Backend.Anthropic – Anthropic Messages API provider (Claude) and Anthropic-compatible endpoints
NetCoreAI.VectorStore.Sqlite    – default vector store (sqlite-vec), zero-config
NetCoreAI.VectorStore.Postgres  – pgvector
NetCoreAI.VectorStore.Qdrant    – Qdrant
NetCoreAI.Storage.*             – metadata persistence (SQLite default, SQL Server, PostgreSQL)
NetCoreAI.Client                – typed C# client (IAgentClient, IKnowledgeClient) usable in-process or over HTTP
```

### 6.2 Integration model

```csharp
builder.Services.AddNetCoreAI(o =>
{
    o.DataDirectory = "./netcoreai";          // models, vectors, metadata
    o.Dashboard.Path = "/netcoreai";
    o.Dashboard.Authorization = policy => policy.RequireRole("Admin");
})
.AddGgufBackend()
.AddOnnxBackend()
.AddOllamaBackend()                       // optional: local/LAN Ollama
.AddOpenAICompatibleBackend()             // optional: OpenAI / Azure / vLLM / LM Studio
.AddAnthropicBackend()                    // optional: Claude
.AddSqliteVectorStore();

app.MapNetCoreAI();   // dashboard + management API + agent API
```

Consumer code depends only on `IChatClient`, `IEmbeddingGenerator`, `IAgentClient`, `IKnowledgeClient`.

### 6.3 Layered design

```
┌───────────────────────────────────────────────────────────┐
│ Dashboard UI (Blazor)   │  Agent HTTP API  │  C# Client SDK │
├───────────────────────────────────────────────────────────┤
│ Agent Engine  │ Tool Registry  │ RAG Pipeline │ Chat Sessions│
├───────────────────────────────────────────────────────────┤
│ Model Registry │ Model Hub (HF) │ Hardware Probe │ Downloader│
├───────────────────────────────────────────────────────────┤
│ IModelProvider: ONNX | GGUF | Safetensors | Ollama | OpenAI-compat | Anthropic │
├───────────────────────────────────────────────────────────┤
│ Storage: metadata DB │ vector store │ file store            │
└───────────────────────────────────────────────────────────┘
```

---

## 7. Feature Specification

Priority legend: **P0** = must ship in v1 · **P1** = fast follow · **P2** = designed-for, built later.

### 7.1 Module A — Model Runtime & Providers

#### 7.1.1 Provider abstraction (P0)
- `IModelProvider` with: `SupportedFormats`, `CanLoad(ModelDescriptor)`, `LoadAsync`, `UnloadAsync`, `CreateChatClient`, `CreateEmbeddingGenerator`, `GetCapabilities` (chat, embeddings, tool-calling, vision, JSON mode, max context).
- All chat/embedding access goes through `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`).
- Providers are discovered via DI; the registry selects the provider for a model by format + hardware + user preference.
- Middleware pipeline applied uniformly: function invocation, logging, OpenTelemetry, optional caching, rate limiting.

**Acceptance criteria**
- [ ] A model loaded via any provider can be used through the same `IChatClient` call with no provider-specific code.
- [ ] Adding a new provider package requires no change to Core or Dashboard.
- [ ] Capabilities are queryable and the UI hides unsupported features per model (e.g. no tool toggle for a model without tool-calling).

#### 7.1.2 GGUF provider (P0)
- Based on LLamaSharp with selectable native backend (CPU, CUDA 12, Vulkan, Metal).
- Configurable: context size, GPU layer count, batch size, flash attention, KV cache type, threads.
- Chat template applied automatically from GGUF metadata; manual override in UI.
- Grammar-constrained (GBNF) JSON output for structured responses.
- Embedding models (e.g. nomic-embed, bge) supported through the same provider.

#### 7.1.3 ONNX provider (P0)
- Based on `Microsoft.ML.OnnxRuntimeGenAI`; execution providers: CPU, DirectML, CUDA (NPU P1).
- Loads Hugging Face ONNX model folders (genai_config.json).
- Text embedding models via plain ONNX Runtime (sentence-transformers exports).

#### 7.1.4 Safetensors provider (P1 — with constraints)
- Safetensors is a *weight format*, not a runtime. Two supported strategies:
  1. **Convert-on-import (default, P1):** bundled converter turns HF Safetensors repos into GGUF (llama.cpp convert) or ONNX (Optimum export) at download time; the converted artifact is what runs. Requires a bundled converter binary; no Python on the host.
  2. **Native execution (P2):** TorchSharp-based runtime for architectures with .NET implementations (Llama, Phi, Qwen families).
- The Model Hub must clearly label "runs natively" vs "will be converted" before download.

**Open question (engineering, blocking for P1):** ship the llama.cpp converter as a native binary per RID, or run conversion as an optional server-side job? See §12.

#### 7.1.5 Remote & cloud providers (P0)
Remote providers sit beside local models as peers: they appear in the same model registry, are selectable in the chat playground and agent builder, and go through the same `IChatClient` middleware pipeline.

| Provider | Priority | Notes |
|---|---|---|
| **Ollama** | P0 | Local machine or LAN server via `OllamaSharp`; lists models from the server (`/api/tags`), pulls models through Ollama's own library from the Model Hub UI, supports chat, embeddings, tool-calling and streaming |
| **OpenAI-compatible** | P0 | Configurable base URL + API key + model list. Presets for OpenAI, Azure OpenAI (deployment + API version), vLLM, LM Studio, Groq, DeepSeek, OpenRouter, Together, Mistral; "custom" for anything else. Chat, embeddings, tool-calling, JSON mode, vision (P1) |
| **Anthropic** | P0 | Anthropic Messages API (Claude models) with tool use, streaming, system prompts, extended context; supports Anthropic-compatible proxies via configurable base URL. Embeddings are not provided by this API — the UI requires pairing with a local or OpenAI-compatible embedding model |
| Google Gemini, AWS Bedrock, Azure AI Foundry | P2 | Via `IModelProvider` plugins |

**Provider connections**
- Multiple named connections per provider type (e.g. "OpenAI-prod", "Groq-dev", "Office Ollama box").
- Connection test button (auth, reachability, model list) and health status shown in the registry.
- Model discovery: pull the provider's model list where the API supports it; otherwise manual entry with a curated default list per preset.
- Per-connection defaults: timeout, retry policy, max concurrency, rate limit, cost per 1K tokens (for usage reporting).
- Secrets (API keys, tokens) stored via ASP.NET Core Data Protection; never returned to the UI after save; environment-variable override for containers.
- Outbound proxy support and per-connection allow-list; the data-residency switch (§7.9) disables all remote connections globally.

**Routing & fallback**
- Any agent or alias can specify a primary model and an ordered fallback list mixing local and remote (e.g. local Qwen → Ollama → Claude).
- Failover triggers: provider error, timeout, rate-limit, local model not loaded and memory unavailable.
- Optional cost/latency-aware routing rules (P1): route by prompt length, tool requirement, or user role.

**Acceptance criteria**
- [ ] A user can add an OpenAI-compatible, Anthropic and Ollama connection from the dashboard and chat with each in the playground without restarting the host.
- [ ] An agent can use a cloud model for generation and a local model for embeddings in the same run.
- [ ] Disabling remote providers in settings removes them from all selectors and blocks execution with a clear error.
- [ ] Usage dashboard attributes tokens and estimated cost per connection.

#### 7.1.6 Hardware detection & auto-configuration (P0)
- Probe: OS, CPU cores, RAM, GPU vendor/model/VRAM (NVIDIA via NVML, AMD/Intel via DirectML/Vulkan enumeration), NPU presence.
- Recommends quantization and GPU layer split per model; shows "will fit / tight / will not fit" before download and before load.
- Refuses to load a model that exceeds available memory with a clear message instead of crashing the host process.

#### 7.1.7 Model lifecycle (P0)
- Load / unload / warm-up on startup (`IHostedService`) with readiness gate.
- Idle unload timeout (configurable) to release VRAM.
- Concurrency policy per model: single-slot queue (default), context pool of N, or reject-when-busy.
- Multiple models loaded simultaneously subject to memory budget.
- Graceful behaviour on host shutdown (cancel in-flight generations, release native handles).

### 7.2 Module B — Model Hub (Hugging Face browsing & download)

#### 7.2.1 Browse & search (P0)
- Search Hugging Face Hub by name, author, task, format (GGUF / ONNX / Safetensors), license, size, downloads, likes, last updated.
- Curated "Recommended" tab: tested model list (chat + embedding) maintained in a JSON manifest updated from a NetCoreAI-controlled URL, with offline fallback.
- Model detail page: README rendered, files list with sizes, quantization variants, license, gated/private flag, estimated RAM/VRAM per variant, "fits your hardware" badge from §7.1.6.

#### 7.2.2 Download manager (P0)
- Select specific files/variants (e.g. Q4_K_M only), not whole repo.
- Resumable downloads, parallel chunks, bandwidth limit, pause/resume/cancel, queue with priority.
- SHA-256 verification against HF LFS metadata; corrupt files rejected and retried.
- Progress via SignalR to the dashboard; persists across dashboard refresh and host restart.
- Hugging Face token support for gated models (stored encrypted); clear error for licence-acceptance-required repos.
- Mirror / proxy URL configuration for restricted networks.

#### 7.2.3 Import (P0)
- Import from local path or upload (chunked, large-file) for air-gapped installs.
- Import from URL (direct file).
- Auto-detect format, architecture, quantization, context length, chat template from file metadata.

#### 7.2.4 Hub extensibility (P2)
- Additional sources: Ollama library, ModelScope, private/internal model registries via `IModelSource`.

### 7.3 Module C — Local Model Management

#### 7.3.1 Model registry (P0)
- Table of all local models: name, alias, format, provider, size on disk, quantization, context length, capabilities, status (available / loading / loaded / error), last used, usage stats.
- Set default chat model and default embedding model.
- Aliases (`fast`, `quality`, `embed`) resolvable from code: `chatClientFactory.Get("fast")`.
- Per-model default parameters (temperature, top-p, max tokens, system prompt, chat template override, stop sequences).
- Tags and notes.

#### 7.3.2 Storage management (P0)
- Configurable data directory; disk usage dashboard; delete with confirmation; orphan-file cleanup.
- Storage quota with warning thresholds.

#### 7.3.3 Model testing & benchmarks (P1)
- One-click "Test" (load + short generation) with tokens/sec, time-to-first-token, memory used.
- Benchmark history per model/hardware for comparison.

#### 7.3.4 Versioning & updates (P1)
- Detect newer revisions on Hugging Face; update with rollback.

### 7.4 Module D — Chat Playground

#### 7.4.1 Chat UI (P0)
- Select any loaded model (auto-load on select if memory allows).
- Streaming responses, markdown + code rendering, copy, regenerate, edit-and-resend, stop generation.
- Parameter side panel (temperature, top-p, top-k, max tokens, repeat penalty, seed, system prompt).
- Conversation history persisted per user; rename, delete, export (JSON/Markdown).
- Token count and cost/time stats per message.

#### 7.4.2 Compare mode (P1)
- Same prompt to 2–3 models side by side.

#### 7.4.3 Attachments (P1)
- Attach files to a chat; for text/PDF, inline extraction; for vision-capable models, image input (P2).

#### 7.4.4 Embeddable chat widget (P1)
- A Razor component / JS snippet the host app can drop into its own pages, bound to a specific agent, themed via CSS variables.

### 7.5 Module E — RAG & Knowledge Bases

#### 7.5.1 Knowledge base management (P0)
- Create knowledge bases with: embedding model, chunking strategy, vector store, access policy.
- Multiple knowledge bases; an agent may attach several.

#### 7.5.2 Data sources (P0 unless noted)
| Source | Priority | Details |
|---|---|---|
| File upload | P0 | PDF, DOCX, PPTX, XLSX, TXT, MD, HTML, CSV, JSON; OCR for scanned PDFs (P1) |
| Folder watch | P1 | Local/UNC path; incremental re-index on change |
| Web URL / sitemap crawl | P1 | Depth, include/exclude patterns, robots respected |
| SQL database | P0 | Connection via host's `DbContext` or connection string; table/view/query selection; column mapping (content, title, metadata, id); scheduled sync; row-level change detection |
| REST API endpoint | P0 | Uses Tool definitions (§7.6) as a source: call endpoint, map JSON path → documents, schedule |
| Host-provided `IKnowledgeSource` | P0 | Developers push documents from code (e.g. on entity save) |
| SharePoint / Google Drive / S3 | P2 | Connector packages |

#### 7.5.3 Ingestion pipeline (P0)
- Stages: extract → clean → chunk → embed → store, each replaceable via interface.
- Chunking strategies: fixed size with overlap, recursive by structure (headings/paragraphs), sentence-aware, per-row (tabular); configurable per source.
- Metadata captured per chunk: source, document id, title, page/section, timestamp, custom fields, ACL tags.
- Background job runner with progress, retry, failure log; cancellable.
- Deduplication by content hash; re-index only changed documents.

#### 7.5.4 Retrieval (P0)
- Vector similarity (cosine) with top-k, score threshold, metadata filters.
- Hybrid search (BM25 + vector) with reciprocal rank fusion (P1).
- Re-ranking with a local cross-encoder model (P1).
- Query rewriting / HyDE (P2).
- Retrieval respects ACL tags: results are filtered by the calling user's claims.

#### 7.5.5 Document chat panel (P0)
- Chat over one or more knowledge bases with citations (source, page, snippet) rendered inline and expandable.
- "Show retrieved chunks" debug view with scores.
- Retrieval settings (top-k, threshold, filters) adjustable per session.

#### 7.5.6 Evaluation (P1)
- Question/answer test sets per knowledge base; run and score (retrieval hit rate, faithfulness via LLM judge).

#### 7.5.7 Vector store providers (P0: SQLite; P1: Postgres, Qdrant; P2: SQL Server 2025 vector, Redis)
- `IVectorStore` abstraction; migration tool between stores.

### 7.6 Module F — Tool Designer (existing API endpoints → AI tools)

#### 7.6.1 Endpoint discovery (P0)
- Auto-discover the host app's endpoints from `EndpointDataSource` and OpenAPI document (controllers and minimal APIs): route, method, parameters, request/response schemas, auth requirements, XML doc summaries.
- Import external OpenAPI 3.x specs for third-party APIs.
- Manual tool definition (URL template + JSON schema) for anything not discoverable.

#### 7.6.2 Tool definition editor (P0)
- Choose an endpoint → generate `AIFunction` metadata: tool name, natural-language description, parameter descriptions, required/optional, enums, defaults.
- Hide/lock parameters (e.g. `tenantId` always taken from the caller's claims, never from the model).
- Response mapping: select which fields return to the model (avoid flooding context), max response size, truncation strategy.
- Invocation mode: **in-process** (direct call through the host's DI/endpoint pipeline, preserving `HttpContext` user) or **HTTP** (loopback or external URL).
- Auth propagation: forward caller's identity / bearer token; or fixed service credential stored encrypted.
- Safety flags: read-only vs side-effecting; side-effecting tools require confirmation policy (auto, ask user, admin-only) at agent level.

#### 7.6.3 Code-defined tools (P0)
- `[AITool]` attribute on host methods, or `services.AddAITool<T>()`, discovered automatically and shown in the designer as read-only.

#### 7.6.4 Tool testing (P0)
- Test panel: fill parameters manually or let a model generate them from a sample prompt; show request, response, latency, errors.

#### 7.6.5 Tool groups & versioning (P1)
- Group tools into toolsets; version tool definitions; deprecate with warnings on agents using them.

#### 7.6.6 Built-in tools (P1)
- Knowledge search (any KB), current date/time, calculator, HTTP fetch (allow-listed domains), SQL read-only query against a configured connection (with row limits).

#### 7.6.7 MCP (P2)
- Consume MCP servers as tool sources; expose NetCoreAI tools/agents as an MCP server.

### 7.7 Module G — Agent Builder

#### 7.7.1 Agent definition (P0)
- Name, description, avatar, model (with fallback model), system prompt (templated with variables), parameters, tools (from §7.6), knowledge bases (from §7.5) with retrieval settings, memory policy, output mode (text / JSON schema), guardrails, access policy.
- Prompt variables can be bound to caller claims, request metadata, or static values.

#### 7.7.2 Agent behaviour (P0)
- ReAct-style tool loop with configurable max iterations and per-tool timeouts.
- Automatic RAG: retrieve from attached KBs before answering, with citation output.
- Conversation memory: sliding window, summarised history (P1), and long-term user memory via KB (P2).
- Structured output enforcement (JSON schema → grammar for GGUF, validation+retry otherwise).

#### 7.7.3 Guardrails (P1)
- Input/output content rules (regex, blocklists, max length), PII masking, prompt-injection heuristics, tool allow-list per user role, token/cost budgets per session and per day.

#### 7.7.4 Agent testing (P0)
- Playground per agent with trace view: each turn shows retrieval results, tool calls with arguments/results, tokens, latency.
- Saved test conversations; regression run (P1).

#### 7.7.5 Versioning & publishing (P1)
- Draft → published versions; rollback; changelog; environment promotion (export/import JSON).

#### 7.7.6 Multi-agent (P2)
- Agent-as-tool (one agent can call another); handoff patterns.

### 7.8 Module H — Agent Runtime & Integration API

#### 7.8.1 C# client (P0)
```csharp
public interface IAgentClient
{
    Task<AgentResponse> RunAsync(string agentId, AgentRequest request, CancellationToken ct = default);
    IAsyncEnumerable<AgentEvent> RunStreamingAsync(string agentId, AgentRequest request, CancellationToken ct = default);
    Task<T> RunAsync<T>(string agentId, AgentRequest request, CancellationToken ct = default); // structured output
}
```
- Resolvable from DI in the host; sessions persisted; `AgentRequest` carries user identity, session id, variables, attachments.
- `IKnowledgeClient` for programmatic ingest/search; `IChatClientFactory` for raw model access by alias.

#### 7.8.2 HTTP API (P0)
- `POST /netcoreai/api/agents/{id}/run` (JSON), `.../run/stream` (SSE), sessions CRUD, feedback endpoint.
- OpenAI-compatible `POST /netcoreai/v1/chat/completions` where `model` = agent id or model alias, so existing OpenAI SDKs work against local agents (P1).
- API keys with scopes (per agent / per KB), rate limits, IP allow-list; managed in dashboard.
- OpenAPI document published for the NetCoreAI API itself.

#### 7.8.3 Events & hooks (P1)
- `IAgentEventHandler` for host code to observe/intercept runs (before tool call, after response) — e.g. audit, custom policy.
- Webhooks on run completed / failed.

#### 7.8.4 SignalR hub (P1)
- Real-time streaming for host UIs without SSE handling.

### 7.9 Module I — Security, Identity & Multi-tenancy

- **P0** Dashboard protected by host's authentication; authorization policy configurable; default deny.
- **P0** Roles: `NetCoreAI.Admin` (everything), `NetCoreAI.Builder` (agents/tools/KBs), `NetCoreAI.User` (chat only). Map to host roles/claims.
- **P0** Secrets (HF token, provider API keys, connection strings) encrypted with ASP.NET Core Data Protection; never logged or echoed to the UI.
- **P0** Tool invocation always runs under the caller's identity unless explicitly configured otherwise; the model can never set identity-bearing parameters.
- **P0** Row-level/ACL filtering in retrieval by claims.
- **P1** Multi-tenant mode: tenant resolver; per-tenant models allowed, KBs, agents, quotas, storage paths.
- **P1** Audit log: who created/changed/deleted models, tools, agents, KBs; who ran which agent.
- **P1** Data residency switch: block all remote providers and outbound calls except allow-listed model mirrors.

### 7.10 Module J — Observability & Operations

- **P0** Structured logging (`ILogger`), OpenTelemetry traces/metrics for every generation, retrieval and tool call (GenAI semantic conventions).
- **P0** Dashboard overview: loaded models, memory, active sessions, requests/min, error rate, downloads in progress.
- **P0** Health checks: `/netcoreai/health` (models ready, vector store reachable, disk space).
- **P1** Run history browser with full traces, filters, export.
- **P1** Usage analytics per agent/model/user; token accounting.
- **P1** Alerts (disk low, model load failure, error-rate spike) via host `IEmailSender`/webhook.

### 7.11 Module K — Settings & Administration

- **P0** General: data directory, default models, concurrency, idle unload, telemetry opt-in.
- **P0** Network: HF endpoint/mirror, proxy, bandwidth limit, offline mode.
- **P0** Providers: enable/disable backends, execution provider preference (CPU/DirectML/CUDA/Vulkan), thread count.
- **P0** Configuration sources: `appsettings` + UI overrides persisted in metadata DB; environment-variable overrides for containers.
- **P1** Backup/restore of metadata + vector store; export/import of agents, tools, KBs as JSON bundles.
- **P1** License management (if commercial tiers are introduced).

### 7.12 Module L — Extensibility

- **P0** Public interfaces: `IModelProvider`, `IModelSource`, `IVectorStore`, `IDocumentExtractor`, `IChunker`, `IKnowledgeSource`, `IToolProvider`, `IAgentEventHandler`, `IMetadataStore`.
- **P0** Dashboard extension points: additional nav items/pages via Razor Class Libraries.
- **P1** Plugin manifest and NuGet discovery so third parties can ship providers.
- **P2** Format converters as plugins (`IModelConverter`).

---

## 8. Non-Functional Requirements

| Area | Requirement |
|---|---|
| Platforms | Windows x64 (CPU, DirectML, CUDA), Linux x64 (CPU, CUDA, Vulkan), macOS arm64 (CPU, Metal). Windows arm64 NPU: P1 |
| Deployment | Works in IIS, Kestrel, Docker, Azure App Service (CPU), self-hosted GPU boxes. Native binaries selected by RID at restore time |
| Host impact | Zero behaviour change to the host app when NetCoreAI is registered but unused. No global filters or middleware injected outside `/netcoreai` |
| Performance | Time-to-first-token < 1 s on a warm 3B Q4 model on 8-core CPU; dashboard pages < 300 ms server render; ingestion ≥ 50 pages/min on CPU embeddings |
| Memory safety | Never exceed configured memory budget; native handles released deterministically; no host crash on model load failure |
| Reliability | Downloads and ingestion jobs survive restart; in-flight generations cancelled cleanly |
| Offline | Full functionality with no internet after models are present; Hub browsing degrades to local cache |
| Data privacy | No telemetry by default; opt-in anonymous usage stats only |
| Accessibility | Dashboard meets WCAG 2.1 AA basics (keyboard nav, contrast, labels) |
| Localisation | Dashboard strings resourced; English + Bengali in v1, others via resource files |
| Versioning | SemVer; `Abstractions` package guarantees no breaking changes within a major |
| Testing | Unit tests for core; integration tests with a small GGUF fixture (< 500 MB) and ONNX fixture in CI; contract tests for HTTP API |
| Licensing | Core under a permissive licence compatible with LLamaSharp (MIT) and ONNX Runtime (MIT); model licences surfaced in UI and must be acknowledged for gated/non-commercial models |

---

## 9. Data Model (metadata store)

```
Models        (Id, Alias, Name, Format, Provider, ConnectionId?, Path?, SizeBytes?, Quantization, ContextLength,
               Capabilities, Family, ChatTemplate, DefaultParams, Source, Revision, Sha256, Status,
               Tags, CreatedAt, LastUsedAt)
Downloads     (Id, ModelId?, RepoId, Files[], BytesTotal, BytesDone, State, Error, StartedAt)
ProviderConnections (Id, Name, Type[Ollama|OpenAICompatible|Anthropic|...], Preset, BaseUrl,
               EncryptedSecret, DefaultParams, Timeout, RetryPolicy, MaxConcurrency, CostPer1KTokens,
               Enabled, LastHealthCheckAt, HealthStatus)
KnowledgeBases(Id, Name, EmbeddingModelId, VectorStore, ChunkingConfig, AccessPolicy, CreatedAt)
DataSources   (Id, KnowledgeBaseId, Type, Config(json), Schedule, LastSyncAt, State)
Documents     (Id, KnowledgeBaseId, DataSourceId, ExternalId, Title, ContentHash, Metadata, AclTags, IndexedAt)
Chunks        (vector store: ChunkId, DocumentId, Text, Embedding, Metadata)
Tools         (Id, Name, Description, Kind[Endpoint|OpenApi|Code|Builtin], Definition(json),
               ParameterSchema, LockedParams, ResponseMapping, InvocationMode, AuthMode,
               SideEffecting, Version, State)
Agents        (Id, Name, Description, ModelId, FallbackModelId, SystemPrompt, Params, ToolIds[],
               KnowledgeBaseIds[], RetrievalConfig, MemoryPolicy, OutputSchema, Guardrails,
               AccessPolicy, Version, State)
Sessions      (Id, AgentId?, ModelId?, UserId, Title, CreatedAt, UpdatedAt)
Messages      (Id, SessionId, Role, Content, ToolCalls, Citations, Tokens, LatencyMs, CreatedAt)
Runs          (Id, AgentId, SessionId, UserId, Trace(json), Status, Tokens, DurationMs, CreatedAt)
ApiKeys       (Id, Name, HashedKey, Scopes, RateLimit, ExpiresAt, CreatedBy)
AuditLog      (Id, Actor, Action, EntityType, EntityId, Before, After, At)
Settings      (Key, Value, Scope[Global|Tenant])
```

---

## 10. API Surface (v1 summary)

| Area | Endpoints |
|---|---|
| Hub | `GET /api/hub/search`, `GET /api/hub/models/{repo}`, `POST /api/hub/download`, `GET/DELETE /api/downloads/{id}` |
| Models | `GET/POST/PUT/DELETE /api/models`, `POST /api/models/{id}/load|unload|test`, `GET /api/hardware` |
| Providers | `GET/POST/PUT/DELETE /api/providers/connections`, `POST /api/providers/connections/{id}/test`, `GET /api/providers/connections/{id}/models` |
| Chat | `POST /api/chat` (stream), `GET/POST/DELETE /api/sessions` |
| Knowledge | `GET/POST/PUT/DELETE /api/kb`, `POST /api/kb/{id}/sources`, `POST /api/kb/{id}/ingest`, `POST /api/kb/{id}/search`, `GET /api/kb/{id}/jobs` |
| Tools | `GET /api/tools/discover`, `POST /api/tools/import-openapi`, `GET/POST/PUT/DELETE /api/tools`, `POST /api/tools/{id}/test` |
| Agents | `GET/POST/PUT/DELETE /api/agents`, `POST /api/agents/{id}/run`, `POST /api/agents/{id}/run/stream`, `GET /api/agents/{id}/runs` |
| Compat | `POST /v1/chat/completions`, `POST /v1/embeddings` (P1) |
| Admin | `GET/PUT /api/settings`, `GET/POST/DELETE /api/apikeys`, `GET /api/audit`, `GET /health` |

All paths are prefixed by the configured dashboard path (default `/netcoreai`).

---

## 11. Phasing

| Phase | Scope | Exit criterion |
|---|---|---|
| **Phase 1 — Foundation** (P0 subset) | Abstractions, GGUF + ONNX providers, Ollama / OpenAI-compatible / Anthropic providers with connection management, hardware probe, model registry, Hub browse/download/import, Chat playground, dashboard shell with auth, settings, health | G1 and G2 demonstrated on Windows + Linux |
| **Phase 2 — Knowledge** (P0) | KB management, file + SQL + code sources, ingestion pipeline, SQLite vector store, document chat with citations, ACL filtering | Chat over a 500-page PDF set with correct citations |
| **Phase 3 — Tools & Agents** (P0) | Endpoint discovery, tool editor and testing, code-defined tools, agent builder, agent playground with trace, `IAgentClient`, HTTP run API, API keys, OTel | G3 and G4 demonstrated |
| **Phase 4 — Hardening** (P1) | Safetensors convert-on-import, cost/latency-aware routing, hybrid search + re-rank, guardrails, versioning, audit, analytics, OpenAI-compatible API, embeddable widget, Postgres/Qdrant stores, multi-tenant | Public 1.0 release |
| **Phase 5 — Expansion** (P2) | Native Safetensors runtime, MCP in/out, multi-agent, vision input, more connectors, additional hub sources | Roadmap-driven |

---

## 12. Open Questions

| # | Question | Owner | Blocking? |
|---|---|---|---|
| 1 | Safetensors: bundle native converter binaries per RID (adds ~100 MB to package set) vs. a separate `NetCoreAI.Tools.Converter` optional package vs. TorchSharp-only path? | Engineering | Blocks Phase 4 design |
| 2 | Dashboard UI technology: Blazor Server (simplest to embed, needs SignalR) vs. Blazor WebAssembly (static, larger payload) vs. prerendered Razor + minimal JS? | Engineering | Blocks Phase 1 |
| 3 | Default metadata store: SQLite file in data directory (zero-config) — is this acceptable for load-balanced deployments, or must v1 require a shared DB? | Engineering / Product | Blocks Phase 1 |
| 4 | In-process tool invocation: call the endpoint through the pipeline (`IEndpoint` + fake `HttpContext`) or require developers to opt endpoints in via attribute for safety? | Engineering | Blocks Phase 3 |
| 5 | Business model: fully OSS, open-core (multi-tenant, audit, guardrails paid), or commercial licence? Affects package split and licensing text | Product / Business | Before Phase 4 |
| 6 | Hugging Face API rate limits and ToS for server-side proxying of search — do we call HF from the browser (CORS) or from the server (needs token for volume)? | Engineering / Legal | Non-blocking |
| 7 | Minimum hardware baseline to officially support (e.g. 8 GB RAM CPU-only)? Determines curated model list | Product | Non-blocking |
| 8 | Name check: "NetCoreAI" trademark/NuGet id availability | Business | Non-blocking |

---

## 13. Success Metrics

| Type | Metric | Target (90 days post 1.0) |
|---|---|---|
| Leading | Installs completing first chat (telemetry opt-in cohort) | ≥ 70 % |
| Leading | Median time install → first chat | ≤ 15 min |
| Leading | % of installs that create ≥ 1 knowledge base | ≥ 40 % |
| Leading | % of installs that publish ≥ 1 agent | ≥ 25 % |
| Lagging | GitHub stars / NuGet downloads | 1 000 stars, 20 000 downloads |
| Lagging | Community-contributed providers or connectors | ≥ 3 |
| Lagging | Support issues per 100 installs | ≤ 5 |

---

## 14. Glossary

| Term | Meaning |
|---|---|
| Provider / Backend | Runtime that executes a model format locally (ONNX, GGUF, etc.) or a connector to a remote model service (Ollama, OpenAI-compatible, Anthropic) |
| Provider connection | A named, credentialed configuration of a remote provider (base URL, key, defaults) |
| Model Hub | Browsing/download UI over Hugging Face and other sources |
| Knowledge Base (KB) | A collection of indexed documents with one embedding model and vector store |
| Data Source | A configured origin of documents for a KB (files, SQL, API, code) |
| Tool | A callable function exposed to a model, backed by an endpoint, code, or built-in |
| Agent | Model + system prompt + tools + knowledge bases + policies, callable by id |
| Run | One execution of an agent for a request, with a full trace |
| ACL tag | Metadata on documents/chunks used to filter retrieval by caller claims |
