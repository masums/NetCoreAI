# Development plans

One plan per phase of [TASKS.md](../../TASKS.md). Each plan turns the checklist into an ordered sequence of work packages with design notes, dependencies, test strategy and a definition of done. Update the plan when the design changes; update `TASKS.md` when work lands.

| Phase | Plan | Exit criterion |
|---|---|---|
| 0 | [Repo & solution scaffolding](phase-0-scaffolding.md) | `dotnet build`/`test`/`pack` green; samples build from local feed |
| 1 | [Foundation](phase-1-foundation.md) | G1 (≤ 15 min to first local chat) and G2 (no external deps) on Windows + Linux |
| 2 | [Knowledge](phase-2-knowledge.md) | Chat over a 500-page PDF set with correct citations |
| 3 | [Tools & Agents](phase-3-tools-agents.md) | G3 (endpoint → tool ≤ 5 min) and G4 (same agent via C# and HTTP) |
| 4 | [Hardening → 1.0](phase-4-hardening.md) | Public 1.0 |
| 5 | [Expansion](phase-5-expansion.md) | Roadmap-driven |

## Principles that apply to every phase

1. **Host-first.** NetCoreAI is a library mounted inside someone else's ASP.NET Core app. Nothing may change host behaviour outside the configured path; every service registration is idempotent (`TryAdd`), every endpoint lives in one group with the host's authorization policy.
2. **`Microsoft.Extensions.AI` is the only model API.** Consumers see `IChatClient` / `IEmbeddingGenerator`; providers adapt to it. Provider packages never leak their SDK types through `Abstractions`.
3. **Abstractions is sacred.** Interfaces and records only, no implementation, no breaking changes within a major. Anything experimental lives in `Core` first.
4. **Conformance tests before providers.** A provider or vector store is "done" when it passes `NetCoreAI.Conformance` against a fixture, not when it demos.
5. **Vertical slices.** Each work package ends with something callable end-to-end (API → service → storage), with tests, before the next starts.
6. **Samples consume packages.** `samples/` never project-references `src/`; CI packs first then builds samples against the local feed.
