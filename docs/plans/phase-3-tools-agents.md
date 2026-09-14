# Phase 3 — Tools & Agents

**Goal:** the host's existing endpoints become model tools in minutes; agents combine model + prompt + tools + KBs and behave identically via `IAgentClient` and HTTP.
**Exit criterion:** G3 (endpoint → tool ≤ 5 min) and G4 (same agent, same output via C# and HTTP) demonstrated.

## Decision required first: open question 4 (in-process tool invocation)
**Decision (ADR-0004, written at WP3.1):** in-process invocation calls the discovered endpoint's `RequestDelegate` through the real pipeline with a synthetic `HttpContext` (`DefaultHttpContext` carrying the caller's `ClaimsPrincipal`, a `RequestServices` scope, JSON body and route values from tool args), only for endpoints the developer opted in with `[AIToolEndpoint]` / `.WithAITool()` metadata or that an Admin explicitly enabled in the designer (stored flag, audited). HTTP loopback mode is the fallback for everything else. Rationale: safe by default plus zero friction for the opt-in path.

## Architecture
```
EndpointDiscovery (EndpointDataSource + OpenAPI doc) --> ToolDefinition (Kind: Endpoint | OpenApi | Code | Manual)
ToolRegistry: ToolDefinition -> AIFunction (AIFunctionFactory) with locked params bound from claims
AgentEngine: system prompt (templated) -> RagChatClient -> FunctionInvokingChatClient (loop, max iters, timeouts) -> structured output
AgentRuntime: sessions, memory window, traces (Runs table), events (IAgentEventHandler, P1)
IAgentClient (in-process) / HttpAgentClient  ->  POST /api/agents/{id}/run | /run/stream (SSE)
```
Abstractions added: `ToolDefinition`, `IToolProvider`, `[AITool]`, `AgentDefinition`, `AgentRequest` / `AgentResponse` / `AgentEvent`, `IAgentClient`, `IAgentEventHandler`, `RunTrace`, `ApiKey`.

## Work packages
### WP3.1 Endpoint discovery + ADR-0004 — done
`EndpointDiscoveryService` reads `EndpointDataSource` (route pattern, methods, parameter metadata from ApiExplorer / endpoint metadata, authorization metadata, XML doc summaries via the `Microsoft.AspNetCore.OpenApi` document if registered). `GET /api/tools/discover`. Import external OpenAPI 3.x → `OpenApi` kind tools. Read with `System.Text.Json` rather than `Microsoft.OpenApi.Readers`: only paths, operations, parameters and the body shape matter here, and pulling a full parser plus its YAML dependency into the package every host references is a real cost for one import feature. YAML documents are refused with a message saying to convert them. Manual definition (URL template + JSON schema).

### WP3.2 Tool definitions + registry — done (the designer UI lands with WP3.4)
`ToolDefinition` entity; editor generates `AIFunction` metadata (name, description, parameter docs/required/enums/defaults from schema); locked/hidden params bound to claim type, request metadata or static; response mapping (JSON-path select, max bytes, truncation strategy); invocation mode (InProcess per ADR-0004 / HTTP loopback / HTTP external); auth propagation (forward caller bearer/cookie, or encrypted service credential); safety flags (ReadOnly vs SideEffecting + confirmation policy Auto / AskUser / AdminOnly). `ToolRegistry` builds `AIFunction`s per request with the caller's `ClaimsPrincipal` captured in the closure. The model never sees locked params (invariant test).

### WP3.3 Code-defined tools — done
`[AITool]` on methods + `AddAITool<T>()`. No assembly-wide scan: registration is per type, so a tool exists because somebody registered it rather than because an attribute happened to be on something loaded. Registering a type with no marked method throws rather than registering nothing silently. wrapped with `AIFunctionFactory.Create`; shown read-only in the designer.

### WP3.4 Tool testing panel — done, with the Tools page (designer, discovery list, OpenAPI import)
`POST /api/tools/{id}/test` with manual args or model-generated args from a sample prompt (uses the default chat model); UI shows request, response, latency, errors.

### WP3.5 Agent definition + engine — definition, engine, runs and traces done; structured-output grammar/retry outstanding
`AgentDefinition` entity + CRUD; prompt templating (`{{claims.email}}`, `{{request.meta.x}}`, static); `AgentEngine.BuildChatClient(agent, caller)` = alias/fallback → `RagChatClient` (attached KBs + retrieval settings, citations) → `FunctionInvokingChatClient` (max iterations, per-tool timeout via linked CTS, confirmation policy hook) → structured output (`ChatResponseFormat.ForJsonSchema`; GGUF converts to GBNF, others validate + retry N). Sliding-window memory from `Sessions` / `Messages`. Every run writes a `Runs` row with a `RunTrace` (retrievals, tool calls with args/results, tokens, latency per step).

### WP3.6 Agent playground — done (runs are saved and re-readable rather than named and pinned)
Interactive page: chat with a trace drawer per turn; saved test conversations; capability-aware UI (no tools toggle for models without ToolCalling).

### WP3.7 `IAgentClient` + HTTP API + API keys — clients, the run API and API keys done; the keys dashboard page, the OpenAPI document and OTel spans outstanding
`NetCoreAI.Client`: `AgentClient` (in-process, DI), `HttpAgentClient` (`AddNetCoreAIClient(baseUrl, apiKey)`), generic `RunAsync<T>`. Endpoints `run`, `run/stream` (SSE `AgentEvent` JSON lines), sessions CRUD, feedback. `ApiKeys` entity (hashed, scopes per agent/KB, rate limit via `RateLimiter`, IP allow-list), `ApiKeyAuthenticationHandler` scoped to `/netcoreai/api`. OpenAPI document for the NetCoreAI API at `/netcoreai/openapi/v1.json`. OTel spans for tool calls and agent runs (`gen_ai.agent.*`, `gen_ai.tool.*`).

### WP3.8 Acceptance
Test: the same agent invoked via `IAgentClient` and via HTTP with temperature 0 on the fake OpenAI server yields identical `AgentResponse` (G4). Timed G3 run documented in `docs/guides/tools.md`.
