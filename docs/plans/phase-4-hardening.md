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
7. **Security & tenancy** — done, bar a dashboard tenant switcher. Audit log, multi-tenancy with quotas, data-residency switch. The audit log covers models, connections, tools, agents, knowledge bases and API keys, and records a guardrail refusal whatever the run setting says — a refusal is not a run, it is somebody being told no. Runs themselves are opt-in (`Audit.IncludeRuns`), because they already have traces and recording both doubles the busiest write path to say the same thing twice. A failed audit write is logged and swallowed: a log that can fail a save is a log that gets switched off the first time it does. The retention sweep that keeps it (and run traces) from growing forever is new too — `PruneAsync` had existed on both stores since Phase 1 and nothing ever called it.
8. **Observability** — usage analytics and the run history browser are done; alerts via host `IEmailSender`/webhook are outstanding.

Both read from the run traces that were already being written. There is no separate accounting table,
because a second copy kept for reporting disagrees with the traces the first time a run is written by a
path that forgot to update it. The figures the Usage page shows are the same rows the run's own trace
shows. Seven columns were lifted out of the JSON so the totals can be done in SQL, all of them nullable —
a run from before those columns existed reads as "not recorded" rather than as zero, and the summary
reports how many of those there were rather than folding them in. That nullability is also what let the
schema upgrade add them to an existing database without asking anyone anything.
9. **Compatibility & embedding** — OpenAI-compatible `/v1/chat/completions` + `/v1/embeddings` (`model` = alias or agent id), embeddable chat widget (Razor component + JS snippet, CSS variables).
10. **Playground P1** — compare mode (2–3 models), attachments (text/PDF inline extraction).
11. **Stores** — `VectorStore.Postgres` (pgvector), `VectorStore.Qdrant`, `Storage.SqlServer`, `Storage.Postgres`, migration tool between vector stores.
12. **Model ops** — testing & benchmarks (tokens/s, TTFT, memory, history), version updates with rollback, backup/restore.
13. **Ecosystem** — plugin manifest + NuGet discovery for third-party providers; `IAgentEventHandler`, webhooks, SignalR hub for host UIs.
14. **Quality** — WCAG 2.1 AA pass, localisation (resx; English + Bengali), docs site, release notes, 1.0 API review of `Abstractions`.

**Multi-tenancy — isolation done, quotas outstanding.** The tenant is part of the primary key of every
tenant-owned table and of a query filter applied in the `DbContext`, so two tenants can both own an agent
called `support` and neither mechanism depends on fifteen stores remembering to filter. Resolvers read a
claim, a subdomain or a header; a request whose tenant cannot be established is refused rather than served
as the default. Vector collections and upload folders carry the tenant, with the default tenant keeping the
names it already had. Quotas are enforced on agents, tools, knowledge bases, documents and uploaded bytes — counted from the
store, so they are exact and cannot drift — plus daily token and cost budgets, which are per process like
every other budget here. Only a *new* one counts against a limit, so editing the agent that reached it
still works. A dashboard tenant switcher is not built; tenants are managed through `/api/tenants`.

This is the one change that needs a database reset: SQLite cannot alter a primary key. Startup detects the
mismatch and refuses by name rather than letting it surface as a UNIQUE constraint failure later. The
schema upgrade otherwise grew a second capability here — it now adds a missing *column* as well as a
missing table, when the column is nullable or has a default, and creates indexes after columns rather than
before (a test caught that ordering).

**Data residency — done, and it was a claim rather than a guarantee before.** `Network.OfflineMode` with
`Network.AllowedHosts` already existed, but the enforcing handler was attached to hub browsing and
downloads only — not to tool invocation or the built-in fetch tool, both of which are a URL a model can
reach. There were also two different definitions of "allowed host": the handler understood `*.example.com`
and the provider check did not, which is a policy with a hole in it by construction. There is now one
`EgressPolicy`, every client NetCoreAI owns goes through it, and a mutation that makes the enforcement a
no-op fails exactly the two tests for the clients that were missing it.

## Release gates for 1.0
Abstractions API frozen (PublicAPI analyzers), conformance suite public, security review of tool invocation + secrets, load test (100 concurrent sessions on a remote provider), upgrade test from 0.x SQLite schema.

**Upgrading a SQLite database — partly done.** A database written by an older version now gains any table
and index this version needs, and is refused by name when a table it already has is missing columns. That
covers what the row design actually produces: a payload of JSON plus the few columns worth filtering on
means a feature adds a table far more often than it changes one. Adding a *column* to an existing table is
still not handled, and is what the remaining gate covers — real EF migrations, once the schema is frozen.
Found by running the sample against a data directory from an earlier build: it died on
`no such table: Jobs`, at whichever query happened to run first.
