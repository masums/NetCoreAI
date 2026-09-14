# ADR-0003: Default metadata store — SQLite via EF Core; shared DB is opt-in

**Status:** Accepted · **Date:** 2026-09-12 · Resolves requirements §12 open question #3 (blocks Phase 1)

## Context
Goal G2 is "zero mandatory external dependencies". Load-balanced hosts (several instances behind a balancer) would each get their own SQLite file, so models, agents and sessions would diverge between instances. Requiring a shared database in v1 would break G1/G2 for the majority single-instance case.

## Decision
- The metadata store is abstracted behind `IMetadataStore` (Abstractions) and implemented with **EF Core**; `NetCoreAI.Storage.Sqlite` is the default and is wired automatically by `AddNetCoreAI()` unless another storage package is registered.
- The SQLite file lives at `{DataDirectory}/netcoreai.db`, WAL mode, migrations applied at startup by a hosted service with a file lock.
- Load-balanced deployments are supported by registering `NetCoreAI.Storage.SqlServer` or `NetCoreAI.Storage.Postgres` (Phase 4) which reuse the same EF Core model. A health-check warning is raised when more than one instance id has written to a SQLite store within the last 5 minutes ("possible multi-instance deployment on SQLite").
- Vector data is **not** stored in the metadata store; it goes through `IVectorStore` (`VectorStore.Sqlite` default).
- Local model files are always on disk under `DataDirectory` and must be on shared/replicated storage in multi-instance setups; the guide documents this.

## Consequences
- Zero-config single instance works out of the box (G1, G2).
- One EF model shared across providers keeps migrations consistent; provider-specific DDL differences are handled by EF.
- Multi-instance correctness is a documented deployment requirement, not a v1 code constraint.
