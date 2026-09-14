# ADR-0004: In-process tool invocation — opt-in, through the real pipeline

**Status:** Accepted · **Date:** 2026-09-14 · Resolves requirements §12 open question #4 (blocks Phase 3)

## Context

A tool built from one of the host's own endpoints has to be invoked somehow when the model calls it. Three options were on the table.

**HTTP loopback** — the host calls itself over the network. Correct by construction: the request goes through the real middleware pipeline, so authentication, authorization, filters, model binding and rate limiting all behave exactly as they do for a browser. It costs a socket, a serialization round trip and a second thread per call, and it needs the host to know its own reachable base address, which behind a reverse proxy or in a container it often does not.

**Calling the handler method directly** — fast and simple, and wrong. It skips every piece of middleware, which means it skips authorization. A tool built this way would run as nobody, or as whatever the developer remembered to pass, and the first time someone wired an admin endpoint to an agent the model would have been handed privileges no user has. Cutting the pipeline out of the path makes the security model a matter of developer discipline, which is exactly what it must not be.

**A synthetic `HttpContext` through the real pipeline** — build a `DefaultHttpContext` carrying the caller's `ClaimsPrincipal`, a request-services scope, and the route values and JSON body derived from the tool arguments, then invoke the matched endpoint's `RequestDelegate`. The pipeline runs, so authorization runs; there is no socket and no base address to discover.

The third is what this decision takes, with a restriction, because a synthetic context is not a browser request: it has no real connection, no TLS details, no client IP, and middleware that reads those (IP allow-lists, forwarded headers, some antiforgery paths) can behave differently from the same call made over a socket. That difference is acceptable when a developer has looked at an endpoint and decided it is safe to expose; it is not acceptable as a default applied to every endpoint the discovery service can see.

## Decision

- In-process invocation runs the discovered endpoint's `RequestDelegate` with a synthetic `HttpContext` carrying the caller's `ClaimsPrincipal` and a scoped `RequestServices`, so **the endpoint's own authorization decides what happens**.
- It is available only for endpoints that are **opted in**: marked `[AIToolEndpoint]` or `.WithAITool()` by the developer, or explicitly enabled by an Admin in the Tool Designer. Enabling one in the designer is stored as a flag on the tool and written to the audit log with who did it.
- **HTTP loopback is the fallback** and the default for everything else, including every imported OpenAPI tool and every endpoint not opted in.
- Whichever mode runs, the invocation carries the **caller's** identity. A tool never runs with more authority than the person who caused it to run, and the model can never supply an identity-bearing argument — those parameters are locked and bound from claims (WP3.2).

## Consequences

- The zero-friction path a developer wants — attribute an endpoint, use it as a tool — is also the fast path, with no socket and no base-address configuration.
- Nothing becomes reachable to a model because it merely exists in the route table. Exposure is always somebody's explicit act, and an Admin's act is recorded.
- Two invocation paths have to be kept behaviourally equivalent. The conformance suite runs the same tool through both modes and asserts the same result, including that an unauthorized caller is refused in both.
- Middleware that depends on real connection details is a documented limitation of in-process mode rather than a surprise: the guide says to leave such endpoints on loopback.
