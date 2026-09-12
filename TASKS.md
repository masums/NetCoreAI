# NetCoreAI — Development Task List

Derived from [docs/requirements/NetCoreAI-Requirements.md](docs/requirements/NetCoreAI-Requirements.md) §11 Phasing.
Per-phase development plans live in [docs/plans/](docs/plans/). Tracks work on the `develop` branch. Check items off as they land; keep phase order — later phases depend on earlier ones.

---

## Phase 0 — Repo & Solution Scaffolding

Not a product phase, but required before Phase 1 can start.

- [x] Resolve Open Question #2 (dashboard tech: Blazor Server vs WASM vs Razor+JS) — blocks Phase 1
- [x] Resolve Open Question #3 (default metadata store: SQLite vs shared DB requirement) — blocks Phase 1
- [x] Create `NetCoreAI.slnx` with `src/` and `tests/` folder structure per README layout
- [x] `Directory.Build.props` + `Directory.Packages.props` (central package management), `global.json` pinned to .NET 10
- [x] Scaffold empty projects: `NetCoreAI.Abstractions`, `NetCoreAI.Core`, `NetCoreAI.Dashboard`, `NetCoreAI.Client`
- [x] Scaffold backend projects: `Backend.Gguf`, `Backend.Onnx`, `Backend.Ollama`, `Backend.OpenAICompatible`, `Backend.Anthropic`
- [x] Scaffold `VectorStore.Sqlite`, `Storage.Sqlite`
- [x] `NetCoreAI.Conformance` test project (contract tests any provider/vector store must pass)
- [x] `NetCoreAI.Core.Tests`, `NetCoreAI.Integration.Tests`
- [x] `samples/` solution: `NetCoreAI.Samples.slnx`, `Directory.Build.props` pinning package version, local NuGet feed wiring
- [x] `MinimalApi` sample (smallest possible host)
- [x] CI pipeline: build → test → pack framework → build samples against local feed
- [x] License, CONTRIBUTING.md, CODE_OF_CONDUCT.md, SECURITY.md stubs (README already links these)

---

## Phase 1 — Foundation

**Exit criterion:** G1 (install → first local chat ≤ 15 min) and G2 (zero mandatory external deps) demonstrated on Windows + Linux.

### Abstractions & Core
- [x] `IModelProvider` interface (§7.1.1): `SupportedFormats`, `CanLoad`, `LoadAsync`, `UnloadAsync`, `CreateChatClient`, `CreateEmbeddingGenerator`, `GetCapabilities`
- [x] Wire all chat/embedding access through `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`)
- [x] Provider registry: DI discovery, selection by format + hardware + user preference
- [x] Middleware pipeline: function invocation, logging, OpenTelemetry, optional caching, rate limiting
- [x] `AddNetCoreAI()` builder extension + options (`DataDirectory`, `Dashboard.Path`, `Dashboard.Authorization`)
- [x] `MapNetCoreAI()` endpoint mapping

### Model Providers (local)
- [ ] GGUF provider on LLamaSharp (§7.1.2): CPU/CUDA12/Vulkan/Metal backends, context size, GPU layers, batch size, flash attention, KV cache config, chat template auto-detect + override, GBNF structured output, embedding model support
- [ ] ONNX provider on `Microsoft.ML.OnnxRuntimeGenAI` (§7.1.3): CPU/DirectML/CUDA, HF ONNX folder loading (`genai_config.json`), sentence-transformers embedding support

### Remote Providers
- [x] Ollama provider via `OllamaSharp` (§7.1.5): model listing (`/api/tags`), chat/embeddings/tool-calling/streaming
- [x] OpenAI-compatible provider: base URL + API key config, presets (OpenAI, Azure OpenAI, vLLM, LM Studio, Groq, DeepSeek, OpenRouter, Together, Mistral, custom)
- [x] Anthropic provider: Messages API, tool use, streaming, system prompts, extended context, configurable base URL for compatible proxies
- [x] Provider connections: multiple named connections per type, connection test (auth/reachability/model list), health status
- [x] Secrets storage via ASP.NET Core Data Protection; env-var override for containers
- [x] Routing & fallback: primary + ordered fallback list (local/remote mixed), failover triggers

### Hardware & Lifecycle
- [x] Hardware probe (§7.1.6): OS, CPU cores, RAM, GPU vendor/VRAM (NVML, DirectML/Vulkan enumeration), NPU presence
- [ ] "Will it fit" quantization/GPU-split recommendation before download/load — *estimator + `/api/hardware/fit` done; pre-download badge needs the Hub (WP1.9)*
- [x] Model lifecycle (§7.1.7): load/unload/warm-up as `IHostedService`, idle unload timeout, concurrency policy (single-slot/pool/reject), multi-model memory budget, graceful shutdown

### Model Hub
- [ ] Hugging Face search/browse (§7.2.1): name/author/task/format/license/size/downloads/likes filters
- [ ] Curated "Recommended" manifest (JSON, remote-fetched with offline fallback)
- [ ] Model detail page: README render, file list, quantization variants, license, fits-hardware badge
- [ ] Download manager (§7.2.2): file/variant selection, resumable + parallel + bandwidth-limited downloads, pause/resume/cancel, SHA-256 verification, SignalR progress, HF token support, mirror/proxy config
- [ ] Import (§7.2.3): local path/upload (chunked), URL import, auto-detect format/arch/quantization/context/chat template

### Local Model Management
- [ ] Model registry table (§7.3.1): metadata, status, aliases (`fast`/`quality`/`embed`), per-model default params, tags/notes — *page + API done; per-model default-parameter editor still API-only*
- [x] `chatClientFactory.Get("alias")` resolution
- [ ] Storage management (§7.3.2): configurable data dir, disk usage dashboard, delete + orphan cleanup, quota warnings

### Chat Playground
- [x] Chat UI (§7.4.1): model selection with auto-load, streaming, markdown/code rendering, copy/regenerate/edit-resend/stop
- [x] Parameter side panel (temperature, top-p, top-k, max tokens, repeat penalty, seed, system prompt)
- [x] Conversation history: persist, rename, delete, export (JSON/Markdown)
- [ ] Token count / cost / time stats per message — *tokens + latency done; cost needs per-connection pricing (WP1.12)*

### Dashboard Shell, Security, Ops (P0 slice)
- [x] Dashboard shell (Blazor per chosen tech), mounted at configurable path
- [x] Auth: host authentication + configurable authorization policy, default deny
- [x] Roles: `NetCoreAI.Admin`, `NetCoreAI.Builder`, `NetCoreAI.User`
- [ ] Settings pages (§7.11 P0 subset): data directory, default models, concurrency, idle unload, telemetry opt-in, network/proxy/offline mode, provider enable/disable, execution provider preference — *general/network/providers done; default-model pickers land with the Hub*
- [x] Structured logging + OpenTelemetry traces/metrics for generation calls
- [x] `/netcoreai/health` health check (models ready, disk space)
- [ ] Dashboard overview page: loaded models, memory, active sessions, requests/min, error rate, downloads in progress — *loaded models, memory, connections, hardware done; requests/min + error rate need the Meter wiring (WP1.12)*

### Phase 1 acceptance
- [x] A model loaded via any provider is usable through the same `IChatClient` call with no provider-specific code
- [x] Adding a new provider package requires no change to Core or Dashboard
- [x] Capabilities are queryable; UI hides unsupported features per model
- [x] Add OpenAI-compatible, Anthropic, and Ollama connections from the dashboard and chat with each in the playground without restarting the host
- [x] Disabling remote providers removes them from selectors and blocks execution with a clear error
- [ ] Onboarding timing test: clean Windows + Linux machine, `dotnet add package` → first local chat response ≤ 15 minutes

---

## Phase 2 — Knowledge

**Exit criterion:** Chat over a 500-page PDF set with correct citations.

- [ ] Knowledge base management (§7.5.1): create KB (embedding model, chunking strategy, vector store, access policy), multiple KBs per agent
- [ ] Data source: file upload (§7.5.2) — PDF, DOCX, PPTX, XLSX, TXT, MD, HTML, CSV, JSON
- [ ] Data source: SQL database — connection via host `DbContext`/connection string, table/view/query selection, column mapping, scheduled sync, row-level change detection
- [ ] Data source: REST API endpoint — reuse Tool definitions (Phase 3 dependency: stub minimal tool-call mechanism or sequence after §7.6 basics), JSON-path → document mapping, schedule
- [ ] Data source: host-provided `IKnowledgeSource` for programmatic push
- [ ] Ingestion pipeline (§7.5.3): extract → clean → chunk → embed → store stages, each swappable via interface
- [ ] Chunking strategies: fixed+overlap, recursive by structure, sentence-aware, per-row tabular
- [ ] Chunk metadata: source, doc id, title, page/section, timestamp, custom fields, ACL tags
- [ ] Background job runner: progress, retry, failure log, cancellable
- [ ] Deduplication by content hash; re-index only changed documents
- [x] `IVectorStore` abstraction + SQLite (sqlite-vec) implementation, zero-config
- [ ] Retrieval (§7.5.4): cosine similarity, top-k, score threshold, metadata filters
- [ ] ACL-filtered retrieval by caller claims
- [ ] Document chat panel (§7.5.5): citations (source/page/snippet) inline + expandable, "show retrieved chunks" debug view, per-session retrieval settings
- [ ] `IKnowledgeClient` for programmatic ingest/search (C# client surface)
- [ ] `GET/POST/PUT/DELETE /api/kb`, `/api/kb/{id}/sources`, `/api/kb/{id}/ingest`, `/api/kb/{id}/search`, `/api/kb/{id}/jobs` endpoints

---

## Phase 3 — Tools & Agents

**Exit criterion:** G3 (expose an endpoint as a tool ≤ 5 min) and G4 (agent behaves identically via C# and HTTP) demonstrated.

### Tool Designer
- [ ] Endpoint discovery (§7.6.1) from `EndpointDataSource` + OpenAPI doc: route, method, params, schemas, auth requirements, XML doc summaries
- [ ] Import external OpenAPI 3.x specs
- [ ] Manual tool definition (URL template + JSON schema)
- [ ] Resolve Open Question #4 (in-process tool invocation strategy) before building invocation modes
- [ ] Tool definition editor (§7.6.2): `AIFunction` metadata generation, name/description/param docs, hide/lock parameters (e.g. `tenantId` from claims), response field mapping + truncation, invocation mode (in-process vs HTTP), auth propagation, safety flags (read-only vs side-effecting + confirmation policy)
- [ ] Code-defined tools (§7.6.3): `[AITool]` attribute, `services.AddAITool<T>()`, auto-discovery as read-only in designer
- [ ] Tool testing panel (§7.6.4): manual params or model-generated from sample prompt, request/response/latency/errors

### Agent Builder
- [ ] Agent definition (§7.7.1): name, description, avatar, model + fallback, templated system prompt, params, tools, KBs with retrieval settings, memory policy, output mode, guardrails, access policy
- [ ] Prompt variable binding: caller claims, request metadata, static values
- [ ] Agent behaviour (§7.7.2): ReAct-style tool loop with max iterations + per-tool timeouts, automatic RAG retrieval + citations, sliding-window memory, structured output (JSON schema → grammar for GGUF, validate+retry otherwise)
- [ ] Agent testing playground (§7.7.4): trace view (retrieval, tool calls w/ args+results, tokens, latency), saved test conversations

### Agent Runtime & Integration API
- [ ] `IAgentClient` (§7.8.1): `RunAsync`, `RunStreamingAsync`, generic `RunAsync<T>`; DI-resolvable; session persistence
- [ ] `IChatClientFactory` for raw model access by alias
- [ ] HTTP API (§7.8.2): `POST /netcoreai/api/agents/{id}/run`, `.../run/stream` (SSE), sessions CRUD, feedback endpoint
- [ ] API keys: scopes (per agent/KB), rate limits, IP allow-list, dashboard management
- [ ] OpenAPI document for the NetCoreAI API itself
- [ ] Identity/security invariant: tool invocation always runs under caller identity unless explicitly configured; model can never set identity-bearing params
- [ ] OpenTelemetry coverage extended to tool calls and agent runs (GenAI semantic conventions)
- [ ] `GET/POST/PUT/DELETE /api/tools`, `/api/tools/discover`, `/api/tools/import-openapi`, `/api/tools/{id}/test`
- [ ] `GET/POST/PUT/DELETE /api/agents`, `/api/agents/{id}/run(/stream)`, `/api/agents/{id}/runs`

---

## Phase 4 — Hardening (P1) → Public 1.0

- [ ] Resolve Open Question #1 (Safetensors converter distribution strategy)
- [ ] Safetensors convert-on-import (§7.1.4): bundled converter → GGUF/ONNX at download time, Hub UI labels "runs natively" vs "will be converted"
- [ ] Cost/latency-aware routing rules (route by prompt length, tool requirement, user role)
- [ ] Hybrid search (BM25 + vector, reciprocal rank fusion)
- [ ] Re-ranking with local cross-encoder model
- [ ] Guardrails (§7.7.3): input/output content rules, PII masking, prompt-injection heuristics, per-role tool allow-list, token/cost budgets
- [ ] Agent versioning & publishing (§7.7.5): draft → published, rollback, changelog, environment export/import
- [ ] Tool groups & versioning (§7.6.5): toolsets, versioned definitions, deprecation warnings
- [ ] Built-in tools (§7.6.6): knowledge search, date/time, calculator, allow-listed HTTP fetch, read-only SQL query with row limits
- [ ] Audit log (who created/changed/deleted models/tools/agents/KBs, who ran what)
- [ ] Multi-tenant mode: tenant resolver, per-tenant models/KBs/agents/quotas/storage paths
- [ ] Data residency switch: block remote providers/outbound calls except allow-listed mirrors
- [ ] Usage analytics: token accounting per agent/model/user
- [ ] Run history browser with full traces, filters, export
- [ ] Alerts (disk low, load failure, error-rate spike) via host `IEmailSender`/webhook
- [ ] OpenAI-compatible `POST /netcoreai/v1/chat/completions` + `/v1/embeddings`
- [ ] Embeddable chat widget (Razor component/JS snippet, CSS-variable theming)
- [ ] Compare mode (§7.4.2): 2–3 models side by side
- [ ] Chat attachments (§7.4.3): file upload + text/PDF inline extraction
- [ ] `VectorStore.Postgres` (pgvector), `VectorStore.Qdrant`; migration tool between stores
- [ ] Model testing & benchmarks (§7.3.3): one-click test, tokens/sec, TTFT, memory, benchmark history
- [ ] Model versioning & updates (§7.3.4): detect newer HF revisions, update with rollback
- [ ] KB evaluation (§7.5.6): QA test sets, retrieval hit rate, LLM-judge faithfulness scoring
- [ ] Backup/restore of metadata + vector store; JSON bundle export/import for agents/tools/KBs
- [ ] Plugin manifest + NuGet discovery for third-party providers
- [ ] Accessibility pass (WCAG 2.1 AA basics)
- [ ] Localisation: resource files, English + Bengali

---

## Phase 5 — Expansion (P2, roadmap-driven)

- [ ] Native Safetensors runtime (TorchSharp-based, Llama/Phi/Qwen families)
- [ ] MCP: consume MCP servers as tool sources; expose NetCoreAI tools/agents as an MCP server
- [ ] Multi-agent: agent-as-tool, handoff patterns
- [ ] Vision input support (P2 across chat + agents)
- [ ] Additional data sources: folder watch, web URL/sitemap crawl, SharePoint/Google Drive/S3
- [ ] Additional Hub sources: Ollama library, ModelScope, private registries via `IModelSource`
- [ ] Additional remote providers: Google Gemini, AWS Bedrock, Azure AI Foundry
- [ ] Query rewriting / HyDE for retrieval
- [ ] Long-term user memory via KB
- [ ] Format converters as plugins (`IModelConverter`)

---

## Open Questions to resolve along the way (§12)

| # | Question | Blocks |
|---|---|---|
| 1 | Safetensors converter distribution | Phase 4 design |
| 2 | Dashboard UI technology | Phase 1 |
| 3 | Default metadata store for load-balanced deployments | Phase 1 |
| 4 | In-process tool invocation mechanism | Phase 3 |
| 5 | Business model (OSS / open-core / commercial) | Before Phase 4 |
| 6 | HF API proxying (browser CORS vs server-side) | Non-blocking |
| 7 | Minimum supported hardware baseline | Non-blocking |
| 8 | "NetCoreAI" trademark/NuGet id availability | Non-blocking |
