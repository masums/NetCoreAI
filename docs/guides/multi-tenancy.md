# Multi-tenancy

Serving several customers from one host, with each one's models, connections, tools, agents, knowledge
bases, conversations, API keys and audit entries invisible to the others.

Off by default, and switching it on changes nothing about how existing data is stored: **a single-tenant
host is already one tenant**, the one called `default`. There is no "tenancy off" branch anywhere in the
store, which is the point — a second code path is a second thing to get wrong.

```jsonc
"NetCoreAI": {
  "Tenancy": {
    "Enabled": true,
    "ClaimType": "tenant",        // read it from the signed-in identity
    "Header": "X-Tenant",         // or from a header, for service callers
    "FromSubdomain": false,       // or from acme.example.com
    "CreateOnFirstUse": false
  }
}
```

## How a tenant is identified

Every registered resolver is tried in order — claim, then subdomain, then header — and the first answer
wins, so a host can read a claim when somebody is signed in and fall back to a header for service calls.
Write your own for anything else:

```csharp
builder.Services.AddSingleton<ITenantResolver, MyResolver>();
```

**A request whose tenant cannot be established is refused**, not served as the default. Falling back would
hand an unidentified caller somebody's data, which is the only failure in this feature that matters.

A header is whatever the caller puts in it. Only use `Header` where the caller is already trusted to name a
tenant — a gateway that sets it after authenticating, or an API key that is itself scoped to that tenant.
`CreateOnFirstUse` is likewise fine behind a trusted identity provider and dangerous behind a header, where
anyone who can reach the host can bring a tenant into existence.

Tenant ids allow letters, digits, dashes, underscores and dots, up to 64 characters, and may not start with
a dot. They end up in storage paths and vector collection names, so a tenant called `../other` is refused
rather than trusted.

## What isolation actually rests on

Two independent mechanisms, and each one is enough on its own for most reads. Both have tests that fail if
the other is removed.

1. **The tenant is part of the primary key** of every tenant-owned table — `(TenantId, Id)`, not `Id`. So
   two tenants can both have an agent called `support`, and every key lookup is scoped by construction
   rather than by remembering to filter.
2. **A query filter** on every tenant-owned entity, applied in the `DbContext` rather than in each of the
   fifteen stores. Isolation that depends on fifteen classes remembering is isolation that will be wrong
   once.

New rows are stamped with the current tenant in `SaveChanges`, always overwritten rather than filled in
when blank — otherwise writing into another tenant would be a matter of setting a field.

Storage follows: a knowledge base's vector collection and upload folder both carry the tenant, so two
tenants with a base called `docs` do not share one collection. The default tenant keeps the names it
already had, so switching tenancy on does not orphan anything already indexed.

## What is shared and what is not

**Not shared:** models, provider connections, aliases, tools, agents, knowledge bases and their documents,
chat sessions, downloads, jobs, API keys, run traces, audit entries.

**Shared:** settings, and model *files* on disk. The registry row for a model is per tenant — one tenant
does not see another's models — but two tenants registering the same GGUF point at the same file. Copying
twenty gigabytes per tenant is not isolation, it is a disk fire.

Settings are host-wide because the tenant list itself lives there, and a per-tenant list of tenants is the
one arrangement that cannot work.

## Acting as a tenant from code

```csharp
using (tenants.Use("acme"))
{
    await agents.SaveAsync(agent, ct);
}
```

For work with no request behind it — a background job, a startup task, an administrator acting on a
tenant's behalf. Scopes nest and restore what they replaced, not the default. It follows `await`, including
into background work started from a request, which is wanted: that work is still the tenant's.

Do not call it on a request path. The request already has a tenant.

## Upgrading an existing database

**This one needs a reset.** Tenancy changes the primary key of every tenant-owned table, and SQLite cannot
alter a primary key — that is a table rebuild, which is a migration rather than an upgrade.

Startup detects it and refuses by name rather than letting it surface later as a `UNIQUE constraint failed`
in front of whoever happened to save something:

> `Agents is keyed on Id and this version keys it on TenantId + Id`

Delete `netcoreai.db` and its `-wal` and `-shm` files and let it be recreated. You lose saved settings,
conversations, agents, tools and knowledge base definitions; you lose nothing on disk, because models and
uploaded documents are files and are re-indexed. Back the database up first — it is one file.

A fresh install needs none of this, and after this release ordinary upgrades resume: a new table is
created, and a new column is added when it can be filled in. See
[Upgrading](hosting.md#upgrading).

## Quotas

What a tenant may use. Every limit is 0 by default, which means no limit — a quota nobody set must not
start refusing things the day tenancy is switched on.

```csharp
await tenants.UpdateAsync(tenant with
{
    Quota = new TenantQuota
    {
        MaxAgents = 20,
        MaxTools = 50,
        MaxKnowledgeBases = 10,
        MaxDocuments = 5_000,
        MaxUploadBytes = 2L * 1024 * 1024 * 1024,
        MaxTokensPerDay = 1_000_000,
        MaxCostPerDay = 50m,
    },
});
```

`GET /api/tenants/usage` reports what the calling tenant is using against each limit. It reports the
caller's own usage and nobody else's: reading another tenant's would mean stepping outside the tenant the
request established, which is the one thing nothing here does.

Four things worth knowing:

- **Counts are exact**, read from the store rather than from a running total. A cached counter is wrong the
  first time a row is removed by anything other than the path that maintains it.
- **Only a new one counts.** Editing the agent that took a tenant to its limit still works, or the limit is
  a trap rather than a ceiling.
- **Refusals carry the numbers** — "may have 20 agents and already has 20" — because "quota exceeded" tells
  nobody what to delete or what to ask for.
- **Uploads are measured from disk**, not summed from a column, and are checked before the write when the
  stream can say how big it is. A stream that cannot is checked once it is written, and the file is taken
  back off disk before the refusal so a rejected upload does not leave a tenant permanently over.

The two daily budgets — tokens and spend — are counted **in this process only**, the same bargain as the
guardrail budgets and the API key rate limiter: behind a load balancer, *n* instances allow *n* times the
budget, and a restart clears them. They bound a runaway loop rather than a bill. The counts above have no
such caveat.

## Still outstanding

A dashboard **tenant switcher**: tenants are managed through `/api/tenants` and the `ITenantService`, not
yet from a page.
