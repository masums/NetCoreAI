# ADR-0001: Record architecture decisions

**Status:** Accepted · **Date:** 2026-09-12

## Context
NetCoreAI has several open engineering questions (requirements §12) whose answers shape package boundaries and the public API. Contributors need a durable record of why things are the way they are.

## Decision
Use lightweight Architecture Decision Records in `docs/adr/`, numbered sequentially, one decision per file, with sections Context / Decision / Consequences. ADRs are never edited after acceptance; a later ADR supersedes an earlier one.

## Consequences
Every resolved item from §12 gets an ADR before the code that depends on it lands.
