# ADR-0002: Dashboard UI technology — Blazor (Razor Components) with per-page render modes

**Status:** Accepted · **Date:** 2026-09-12 · Resolves requirements §12 open question #2 (blocks Phase 1)

## Context
The dashboard is mounted inside a host application the way Hangfire mounts its dashboard. Options considered:

| Option | Pros | Cons |
|---|---|---|
| Blazor Server / interactive server render mode | Rich interactivity (streaming chat, download progress) with C# only; components are reusable by hosts; SignalR already needed for download progress | Needs a SignalR circuit; host must call `AddRazorComponents().AddInteractiveServerComponents()`; two Blazor apps in one host need care |
| Blazor WebAssembly | Static hosting, no circuit | Multi-MB payload, duplicated model types, slow first load, awkward auth integration with host cookies |
| Prerendered Razor Pages + minimal JS | Zero coupling with host, smallest footprint | Every interactive feature (streaming chat, progress, editors) needs hand-written JS; slows Phases 1–3 significantly |

## Decision
Ship the dashboard as a Razor Class Library of **Razor Components** using **static server-side rendering by default** and the **interactive server render mode only on pages that need it** (chat playground, download manager, agent playground, tool tester).

- `MapNetCoreAI()` maps the components under the configured path (default `/netcoreai`) and registers the Blazor hub on a **dedicated, prefixed hub path** (`/netcoreai/_blazor`) so it does not collide with a host that already uses Blazor.
- `AddNetCoreAI()` calls `AddRazorComponents().AddInteractiveServerComponents()` idempotently; hosts that already registered Razor Components are unaffected.
- Static assets are served from the RCL under `/netcoreai/_content/...`; no global static-file middleware is added.
- All dashboard endpoints are attached to one endpoint group that carries the host's authorization policy (default deny), satisfying the "no global filters or middleware outside `/netcoreai`" requirement (§8).
- Hosts that cannot run a SignalR circuit (some reverse proxies without WebSocket support) get long-polling automatically; a documented `Dashboard.InteractiveMode = None` switch degrades interactive pages to SSR + fetch/SSE for a later release.

## Consequences
- Fastest route to Phase 1–3 UI with one language.
- We own the constraint that the dashboard requires the `Microsoft.AspNetCore.App` framework reference; the framework already needs it for endpoints.
- Chat streaming uses the circuit rather than SSE inside the dashboard; the public HTTP API still exposes SSE for external clients.
- The `docs/guides/hosting.md` guide must document coexistence with an existing Blazor host (separate hub path, `@rendermode` isolation).
