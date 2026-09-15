# Phase 4 — Hardening (P1) → Public 1.0

**Goal:** production readiness: security, multi-tenancy, observability, additional stores, compatibility API, and the Safetensors story.
**Exit criterion:** 1.0 released to NuGet; success metrics instrumentation in place.

## Prerequisite decisions
- Open Question #1 (Safetensors converter) → ADR-0005. Recommended: optional `NetCoreAI.Tools.Converter` package shipping per-RID llama.cpp `convert` binaries; conversion runs as a background job; native TorchSharp path stays Phase 5.
- Open Question #5 (business model) must be settled before splitting packages; plan assumes fully OSS MIT with all features in-tree.

## Work packages (grouped; each independently shippable as 0.x minors)
1. **Safetensors convert-on-import** — `IModelConverter`, converter package, Hub labels "runs natively / will be converted", job with progress.
2. **Routing rules** — `RoutingPolicy` (prompt length, tool requirement, user role, cost/latency budgets) evaluated before `FallbackChatClient`.
3. **Retrieval quality** — done. Hybrid search, the re-ranker seam, and KB evaluation.

Hybrid is the default rather than an option, because choosing between the two is a worse default than
having both: a vector search misses an exact token like an error code, and a keyword search misses a
question phrased differently from the document. A store that cannot do keywords falls back to vectors, so
the default works everywhere it lands. Fusion is on rank rather than score — a cosine similarity and a BM25
score are different things measured differently, and normalising one onto the other invents a relationship
that is not there. In SQLite the keyword index is FTS5 over the existing chunk text, kept in step by
database triggers rather than by code that has to remember, and it applies the same access tags as the
vector leg: an index that ignored them would be a way to read a restricted passage by guessing a word in it.

Re-ranking is a seam plus one implementation. Retrieval and ranking are different jobs: a vector search
compares a question and a passage embedded separately that never saw each other, while a cross-encoder
reads the two together and answers one question — does this passage answer that one. It is much better at
it and far too slow for a corpus, so it goes second over a few dozen candidates. The retrieval stage
therefore fetches wider than the answer when a reranker is registered, because the passages worth promoting
are the ones ranked below the cut. On by default and inert with no reranker registered, so a host that adds
one gets the benefit without finding a setting and a host that does not pays nothing. A reranker that
throws costs quality rather than the answer — the candidates were already reasonable, and turning a quality
feature into an availability one would be the wrong trade.

The seam is tested with a stand-in, in the real retrieval path. The ONNX cross-encoder itself has no
automated test: it needs a model download, and the existing model fixtures are already gated behind
`NETCOREAI_TEST_MODELS=1`. That is recorded as outstanding rather than counted as done.

Evaluation exists to make the previous two paragraphs checkable. Run one set of questions with vectors
alone and again with hybrid and a reranker, and the difference is evidence — where a hit rate on its own is
a number whose meaning depends entirely on how the questions were written, which the guide says out loud.
Hit rate and MRR are reported together because they move independently and only one is about answer
quality: retrieval that finds the right document every time, in position eight every time, has a perfect hit
rate and produces bad answers. Questions that found nothing count as zero in the average rather than being
left out, or a configuration that answers one question and fails the rest scores perfectly. Faithfulness is
a model grading a model, so the judge is named in the run and an unparseable reply leaves a gap rather than
a zero — scoring a chatty judge as "completely unsupported" would make it look like a retrieval problem.
4. **Guardrails** — done. Content rules, PII masking, injection heuristics, per-role tool allow-lists, token and cost budgets. Two deviations from the plan above, both deliberate: they sit in the agent run rather than in chat-client middleware, because the input check has to happen before retrieval and before the model is chosen, and the tool allow-list has to narrow the list before the pipeline is built rather than refuse a call after the model has spent one on it; and there is no `IAgentEventHandler` yet, so findings are carried in the run trace and on a counter instead. Everything is off by default, and `GuardrailAction.Ignore` means genuinely off — no scanning, no trace entries. Budgets are counted per process.
5. **Versioning & publishing** — done. Agent versioning, tool groups and versioning, and the JSON bundle.

A bundle carries what a person built — agents, tools, knowledge base definitions and their sources — and
nothing the environment accumulated: no documents, conversations, runs or audit entries, because carrying a
staging server's conversations into production is not a promotion. Connections are named but never carried,
since a bundle is a file people email and commit, and the secret is the whole of a connection's value to
somebody who should not have it. An import defaults to validating and writing nothing, and reports what the
bundle refers to that neither it nor the host has — the failure that costs weeks is an agent arriving
without its tools, running, answering, and being quietly wrong. Imported tools lose their in-process
permission: running inside this host's process is an act by a named administrator here, not something a
file carries across.

Agent publishing is opt-in by the act of publishing rather than by a setting: an agent nobody has published
runs as it is edited, which is what a draft should do and what every agent did before this existed, and the
first publish changes that for good. A flag nobody finds is a feature nobody has. Rollback publishes the old
definition forward as a new version rather than deleting the ones after it — the history records what
happened, and the rollback is itself a thing that happened. Two exceptions are deliberate: `Enabled` is read
from the draft, because having to publish in order to stop something is the wrong way round in an incident;
and a published version missing from the history is refused rather than quietly served from the draft, which
is the one thing publishing promised would not happen.

Building it exposed a bug in `SaveAsync`: the published pointer came from the request, so every save of a
draft silently unpublished the agent. It is taken from the stored record now.

Tools get a lighter treatment than agents, deliberately. A tool is invoked mid-run and two agents cannot
call two versions of one, so the version is a counter of how much the surface has moved under them rather
than a compatibility promise — and it counts only what a model sees: the name, the description, the
parameters. A changed timeout is not a new version. Saving a tool whose surface moved logs which agents use
it, because that is the moment several of them quietly start behaving differently and nobody was asked.
Groups expand when an agent runs rather than when it is saved, so adding a tool to a group reaches every
agent that named it. A deprecated tool is still handed over: removing a capability from a running agent is
worse than letting it use an old tool for another day, since an agent that has quietly lost a tool answers
from memory instead of saying it cannot find out.
6. **Built-in tools** — done. Knowledge search, date/time, calculator, allow-listed HTTP fetch, read-only SQL query with row limits. Fetch and SQL are unregistered until configured; fetch takes an exact-host allow-list, refuses address literals and does not follow redirects; SQL refuses anything but a single SELECT and caps rows.
7. **Security & tenancy** — done, bar a dashboard tenant switcher. Audit log, multi-tenancy with quotas, data-residency switch. The audit log covers models, connections, tools, agents, knowledge bases and API keys, and records a guardrail refusal whatever the run setting says — a refusal is not a run, it is somebody being told no. Runs themselves are opt-in (`Audit.IncludeRuns`), because they already have traces and recording both doubles the busiest write path to say the same thing twice. A failed audit write is logged and swallowed: a log that can fail a save is a log that gets switched off the first time it does. The retention sweep that keeps it (and run traces) from growing forever is new too — `PruneAsync` had existed on both stores since Phase 1 and nothing ever called it.
8. **Observability** — done. Usage analytics, the run history browser, and alerts.

Both read from the run traces that were already being written. There is no separate accounting table,
because a second copy kept for reporting disagrees with the traces the first time a run is written by a
path that forgot to update it. The figures the Usage page shows are the same rows the run's own trace
shows. Seven columns were lifted out of the JSON so the totals can be done in SQL, all of them nullable —
a run from before those columns existed reads as "not recorded" rather than as zero, and the summary
reports how many of those there were rather than folding them in. That nullability is also what let the
schema upgrade add them to an existing database without asking anyone anything.

Alerts watch the three things that go wrong quietly: a full disk, a model that will not load, and a run
failure rate that has climbed. One deviation from the plan, deliberate: there is no `IEmailSender`
integration, because that interface lives in ASP.NET Core Identity and taking a dependency on Identity to
reach it would put it in every host that references NetCoreAI. `AddAlertSink(lambda)` wires a host's own
mailer in one line instead. Most of the work here is not noticing trouble but declining to mention it
twice — the same condition alerts once per quiet period, and an error rate is ignored until enough runs
have happened for it to mean anything.
9. **Compatibility & embedding** — done. The OpenAI-compatible endpoint and the embeddable chat widget.

A translation layer rather than a second API: everything NetCoreAI can do that OpenAI's shape cannot
express stays on the native API, and nothing there invents a field to carry it. `model` accepts a model, an
alias or an agent id, which is the point — a client written against OpenAI gets an agent's prompt, tools and
knowledge bases by changing a string. Only the last user message is taken as the question, because clients
resend the whole conversation and an agent keeps its own memory. Errors use OpenAI's envelope rather than a
problem document, since a client reads `error.message` and would otherwise report "an error occurred" for
everything. Fields the host cannot honour are ignored rather than refused. Not implemented: function
calling through this endpoint, `n > 1`, logprobs, vision.

The widget is a script tag, themed entirely through CSS variables so a host restyles it without touching
the file. It carries no API key and has no attribute for one: a key in a page is a public key, so the
visitor's own session decides whether a run is allowed, and a public assistant needs the host's own
endpoint in front of it. Answers are written as text rather than markup, which also rules out rendered
markdown — a widget that put model output into innerHTML on somebody's page would be a scripting hole with
a friendly face. Cross-origin embedding is out of scope: it needs CORS and a credential story that is not a
cookie, both of which are decisions about a deployment rather than defaults to pick.

Building it found a real bug in the default-deny branch of `ApplyAuthorization`: it is an endpoint filter
rather than an authorization policy, so `.AllowAnonymous()` under it did nothing. Anything marked anonymous
now gets it.
10. **Playground P1** — chat attachments done; compare mode outstanding.

An attachment belongs to the turn it came with: read once, put in front of the model, forgotten. A file
somebody wants answers from repeatedly belongs in a knowledge base, where it gets chunked, embedded, cited
and access-controlled — all of which this deliberately skips, and the guide says so rather than letting
people discover it by uploading the same PDF every morning. The whole text goes into the prompt, so a long
document does not fit and cannot be made to: it is capped, and the cut is announced inside the text where
the model will read it, because a model handed a document that stops mid-sentence answers about the part it
has as though that were the whole thing. Files are introduced as material rather than as instructions — a
document the model reads as instructions is a way to instruct the model by uploading a file, which is the
injection reasoning applied to a caller's file rather than their message.
11. **Stores** — the migration tool is done; `VectorStore.Postgres`, `VectorStore.Qdrant` and the shared metadata stores are outstanding.

The migrator is store-agnostic and needed writing before either new store, since a store nobody can move
onto is a store nobody adopts. It copies vectors rather than re-embedding them: re-generating would cost an
embedding call per chunk and produce different numbers if the model has moved on since, turning a change of
database into a silent change of what the base retrieves. It refuses to merge into a collection the target
already has — two stores holding overlapping chunk ids from different indexing runs produce a collection
that is neither — and it never deletes from the source, so a half-finished migration still has a way back.
Reading a store's chunks back out is an optional `IVectorEnumerable`; a store that cannot is migrated into
rather than out of, and says so instead of reporting success having copied nothing.
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
