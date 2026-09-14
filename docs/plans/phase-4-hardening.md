# Phase 4 — Hardening (P1) → Public 1.0

**Goal:** production readiness: security, multi-tenancy, observability, additional stores, compatibility API, and the Safetensors story.
**Exit criterion:** 1.0 released to NuGet; success metrics instrumentation in place.

## Prerequisite decisions
- Open Question #1 (Safetensors converter) → ADR-0005. Recommended: optional `NetCoreAI.Tools.Converter` package shipping per-RID llama.cpp `convert` binaries; conversion runs as a background job; native TorchSharp path stays Phase 5.
- Open Question #5 (business model) must be settled before splitting packages; plan assumes fully OSS MIT with all features in-tree.

## Work packages (grouped; each independently shippable as 0.x minors)
1. **Safetensors convert-on-import** — `IModelConverter`, converter package, Hub labels "runs natively / will be converted", job with progress.
2. **Routing rules** — `RoutingPolicy` (prompt length, tool requirement, user role, cost/latency budgets) evaluated before `FallbackChatClient`.
3. **Retrieval quality** — BM25 index (SQLite FTS5) + reciprocal rank fusion; cross-encoder re-ranker via ONNX (`bge-reranker-base`); KB evaluation (QA sets, hit rate, LLM-judge faithfulness).
4. **Guardrails** — done. Content rules, PII masking, injection heuristics, per-role tool allow-lists, token and cost budgets. Two deviations from the plan above, both deliberate: they sit in the agent run rather than in chat-client middleware, because the input check has to happen before retrieval and before the model is chosen, and the tool allow-list has to narrow the list before the pipeline is built rather than refuse a call after the model has spent one on it; and there is no `IAgentEventHandler` yet, so findings are carried in the run trace and on a counter instead. Everything is off by default, and `GuardrailAction.Ignore` means genuinely off — no scanning, no trace entries. Budgets are counted per process.
5. **Versioning & publishing** — agents (draft/published/rollback/changelog), tools (versions, deprecation warnings), JSON bundle export/import.
6. **Built-in tools** — done. Knowledge search, date/time, calculator, allow-listed HTTP fetch, read-only SQL query with row limits. Fetch and SQL are unregistered until configured; fetch takes an exact-host allow-list, refuses address literals and does not follow redirects; SQL refuses anything but a single SELECT and caps rows.
7. **Security & tenancy** — audit log (all CRUD + runs), multi-tenant mode (`ITenantResolver`, per-tenant models/KBs/agents/quotas/storage paths), data-residency switch.
8. **Observability** — usage analytics (tokens/cost per agent/model/user/connection), run history browser with trace export, alerts via host `IEmailSender`/webhook.
9. **Compatibility & embedding** — OpenAI-compatible `/v1/chat/completions` + `/v1/embeddings` (`model` = alias or agent id), embeddable chat widget (Razor component + JS snippet, CSS variables).
10. **Playground P1** — compare mode (2–3 models), attachments (text/PDF inline extraction).
11. **Stores** — `VectorStore.Postgres` (pgvector), `VectorStore.Qdrant`, `Storage.SqlServer`, `Storage.Postgres`, migration tool between vector stores.
12. **Model ops** — testing & benchmarks (tokens/s, TTFT, memory, history), version updates with rollback, backup/restore.
13. **Ecosystem** — plugin manifest + NuGet discovery for third-party providers; `IAgentEventHandler`, webhooks, SignalR hub for host UIs.
14. **Quality** — WCAG 2.1 AA pass, localisation (resx; English + Bengali), docs site, release notes, 1.0 API review of `Abstractions`.

## Release gates for 1.0
Abstractions API frozen (PublicAPI analyzers), conformance suite public, security review of tool invocation + secrets, load test (100 concurrent sessions on a remote provider), upgrade test from 0.x SQLite schema.
