# Phase 0 — Repo & solution scaffolding

**Goal:** a buildable, testable, packable monorepo matching the README layout, so Phase 1 work lands as small PRs.
**Exit criterion:** `dotnet build NetCoreAI.slnx`, `dotnet test`, `dotnet pack -o artifacts/packages` and `dotnet build samples/NetCoreAI.Samples.slnx` succeed locally and in CI on Windows + Linux.

## Decisions taken up front
- Open question #2 → [ADR-0002](../adr/0002-dashboard-ui-technology.md) (Blazor Razor Components, interactive server on demand).
- Open question #3 → [ADR-0003](../adr/0003-default-metadata-store.md) (SQLite via EF Core default, shared DB opt-in).
- Target `net10.0` only for v1 (`global.json` pins SDK 10.0.x with `rollForward: latestFeature`).
- Central package management (`Directory.Packages.props`), `TreatWarningsAsErrors`, nullable + implicit usings, SourceLink, deterministic builds, `MinVer`-style version from git tag (`0.1.0-alpha` until tagged).
- Test stack: xunit.v3, FluentAssertions, NSubstitute. Integration tests that need a model are marked `[Trait("Category","Model")]` and skipped unless `NETCOREAI_TEST_MODELS=1`.

## Work packages (in order)

### WP0.1 Root build infrastructure
- `global.json`, `Directory.Build.props` (company/authors/license/repo URL, `LangVersion latest`, nullable, warnings-as-errors, `GenerateDocumentationFile` for `src/*`, `IsPackable=false` default for tests), `Directory.Packages.props` with all pinned versions (see table), `.editorconfig`, `.gitignore`, `nuget.config` (nuget.org only), `NetCoreAI.slnx`.
- Solution folders `src`, `src/Backends`, `src/VectorStores`, `src/Storage`, `tests`.

### WP0.2 Framework projects (empty but packable)
`NetCoreAI.Abstractions`, `NetCoreAI.Core`, `NetCoreAI.Dashboard` (Razor Class Library, `Microsoft.NET.Sdk.Razor`), `NetCoreAI.Client`, `NetCoreAI` meta-package (no code; references Core + Dashboard + Storage.Sqlite + VectorStore.Sqlite),
`Backend.Gguf`, `Backend.Onnx`, `Backend.Ollama`, `Backend.OpenAICompatible`, `Backend.Anthropic`, `VectorStore.Sqlite`, `Storage.Sqlite`.
Each has: package id, description, `README.md` stub packed as `PackageReadmeFile`, `InternalsVisibleTo` tests.

Dependency direction (enforced by project references only):
```
Abstractions ← Core ← Dashboard, Client, Backends.*, Storage.*, VectorStores.*
```
Backends reference `Core` (for provider base classes) but never each other; `Dashboard` never references a backend.

### WP0.3 Test projects
- `NetCoreAI.Core.Tests` (unit), `NetCoreAI.Conformance` (shipped as a *package* too, so third-party providers can reference it; contains abstract test classes `ModelProviderConformanceTests`, `VectorStoreConformanceTests`, `MetadataStoreConformanceTests`), `NetCoreAI.Integration.Tests` (WebApplicationFactory against a test host; provider tests gated by env vars).

### WP0.4 Samples solution
- `samples/NetCoreAI.Samples.slnx`, `samples/Directory.Build.props` with `<NetCoreAIVersion>`, `samples/nuget.config` adding `../artifacts/packages` as source `netcoreai-local` (falls back to nuget.org), `samples/MinimalApi` referencing `NetCoreAI`, `NetCoreAI.Backend.Gguf`, `NetCoreAI.Backend.Ollama` by `$(NetCoreAIVersion)`.

### WP0.5 CI
`.github/workflows/ci.yml`: matrix `ubuntu-latest`/`windows-latest`; steps restore → build → test (`--collect:"XPlat Code Coverage"`) → pack → build samples against local feed → upload packages artifact. A second workflow `release.yml` publishes to NuGet on `v*` tags (dry-run until Open Question #8 is resolved).
`build/` holds `pack.ps1`/`pack.sh` used both locally and by CI.

### WP0.6 Community files
`LICENSE` (MIT), `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1), `SECURITY.md`, `docs/assets/logo.svg` placeholder, issue/PR templates.

## Pinned package versions (2026-09-12)
| Package | Version | Used by |
|---|---|---|
| Microsoft.Extensions.AI(.Abstractions) | 10.10.0 | Abstractions, Core |
| Microsoft.Extensions.AI.OpenAI / OpenAI | 10.10.0 / 2.13.0 | Backend.OpenAICompatible |
| Anthropic (official SDK, has `AsIChatClient`) | 12.47.0 | Backend.Anthropic |
| OllamaSharp (implements `IChatClient`) | 5.4.30 | Backend.Ollama |
| LLamaSharp + Backend.Cpu | 0.27.0 | Backend.Gguf |
| Microsoft.ML.OnnxRuntimeGenAI(.Managed) / OnnxRuntime | 0.15.2 / 1.30.0 | Backend.Onnx |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.12 | Storage.Sqlite |
| Microsoft.Data.Sqlite | 10.0.12 | VectorStore.Sqlite |
| Microsoft.Extensions.Http.Resilience | 10.10.0 | Core (downloads, remote providers) |
| OpenTelemetry.Extensions.Hosting | 1.18.0 | Integration tests only; Core emits via `System.Diagnostics` |
| Markdig | 1.3.2 | Dashboard |
| xunit.v3 / FluentAssertions / NSubstitute | 4.0.0 / 8.10.0 / 6.2.0 | tests |

## Definition of done
- [ ] All items in TASKS.md Phase 0 checked.
- [ ] CI green on both OSes; `artifacts/packages` contains every `NetCoreAI.*.nupkg`.
- [ ] `samples/MinimalApi` runs and serves `/netcoreai/health` (Phase 1 provides the real content).
