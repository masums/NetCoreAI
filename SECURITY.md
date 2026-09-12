# Security Policy

## Supported versions

Pre-1.0: only the latest `0.x` release receives fixes.

## Reporting a vulnerability

Please do not open a public issue. Email masums@gmail.com with a description, reproduction steps and the affected package/version. You will get an acknowledgement within 3 working days and a fix or mitigation plan within 14 days for confirmed issues.

## Scope notes

- Secrets (API keys, HF tokens, connection strings) are encrypted with ASP.NET Core Data Protection and never returned to the UI. A report showing a secret leaking to logs or the UI is always in scope.
- Tool invocation must run under the caller's identity; any way for a model to set an identity-bearing parameter is in scope.
- The dashboard is default-deny; any route under the configured path reachable without the host's authorization policy is in scope.
