# Contributing to NetCoreAI

Thanks for helping. Providers, vector stores, document extractors, dashboard pages, docs and translations are all welcome.

## Development setup

1. Install the .NET 10 SDK (`global.json` pins the feature band).
2. `dotnet build NetCoreAI.slnx` then `dotnet test --solution NetCoreAI.slnx` (Microsoft.Testing.Platform mode, enabled in `global.json`).
   Tests that need a real model are tagged `Category=Model` and only run when `NETCOREAI_TEST_MODELS=1`; they download a < 500 MB fixture on first run.
3. `build/pack.ps1` (or `build/pack.sh`) packs every package into `artifacts/packages`; `samples/` builds against that feed.

## Where things go

- `src/NetCoreAI.Abstractions` — interfaces and records only. Breaking changes need an RFC in Discussions first.
- `src/NetCoreAI.Core` — everything host-independent: registry, lifecycle, hub, downloads, RAG, tools, agents.
- `src/NetCoreAI.Dashboard` — Razor Components UI and management API. Never references a backend package.
- `src/Backends/*`, `src/VectorStores/*`, `src/Storage/*` — one package per implementation; each must pass the matching suite in `tests/NetCoreAI.Conformance`.
- `docs/plans/` — per-phase development plans; `docs/adr/` — decisions. Update the plan when the design changes, `TASKS.md` when work lands.

## Pull request checklist

- [ ] Builds with `TreatWarningsAsErrors` on Windows and Linux.
- [ ] Unit tests added or updated; conformance suite passes for any provider/store touched.
- [ ] No behaviour change for a host outside the configured dashboard path.
- [ ] Public API in `Abstractions` documented with XML comments.
- [ ] `TASKS.md` box checked and, if relevant, the phase plan updated.

## Commit style

Conventional-ish: `feat(core): ...`, `fix(gguf): ...`, `docs: ...`, `test: ...`, `build: ...`.
