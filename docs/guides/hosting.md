# Hosting NetCoreAI inside an existing app

## What gets added to your host

| Registration | Effect |
|---|---|
| `AddNetCoreAI()` | Options, Data Protection, health checks, the metadata store, provider and model registries, the lifecycle hosted service. Idempotent. |
| `MapNetCoreAI()` | One route group at `Dashboard.Path` (default `/netcoreai`) plus `/netcoreai/health`. No global middleware, no global filters, no static-file middleware. |

Dashboard assets are embedded in the package and served from `/netcoreai/_content/...`, so your own static-file configuration is untouched. Pages are rendered server-side with `HtmlRenderer`, so there is no Blazor circuit and a host that already uses Blazor is unaffected. See [ADR-0002](../adr/0002-dashboard-ui-technology.md).

## Authorization

The dashboard is deny-by-default. Configure exactly one of:

```csharp
o.Dashboard.Authorization = p => p.RequireRole("Admin");  // inline policy
o.Dashboard.PolicyName    = "MyExistingPolicy";           // a policy the host already registered
o.Dashboard.AllowAnonymous = true;                        // development only
```

Without one, every request under the path returns 403 with a page explaining what to set. Roles `NetCoreAI.Admin`, `NetCoreAI.Builder` and `NetCoreAI.User` refine what an authorized user can do. Map them to host roles, or to a custom claim type through `o.Dashboard.RoleClaimType`.

## Storage

Metadata lives in `{DataDirectory}/netcoreai.db` (SQLite, WAL). Vector data lives in `{DataDirectory}/vectors.db`. Model files live under `{DataDirectory}/models`.

For load-balanced deployments see [ADR-0003](../adr/0003-default-metadata-store.md): register a shared store instead (SQL Server or PostgreSQL, Phase 4) and put the data directory on shared storage. A warning is logged when several instances write to one SQLite file.

### Upgrading

A database written by an older NetCoreAI gains whatever tables and indexes the new version needs, on the
first start after the upgrade. What was already there is left alone, and the added objects are named in the
log so you can see what happened.

A missing *column* is added too, when that needs no decision about what the rows already there should say —
it is nullable, or it has a default that means what they always meant.

What is *not* handled is a column that is required with no default, a column whose type changed, or a
changed primary key. That cannot be guessed at without risking your
data, so startup stops and names the table and the columns instead. Before 1.0 the answer is to delete
`netcoreai.db` and its `-wal` and `-shm` files and let it be recreated: you lose saved settings,
conversations, knowledge bases and agents, but nothing on disk — models and uploaded documents are files,
and are re-indexed. After 1.0 this becomes a real migration; the schema is not frozen yet.

Back the database up before upgrading if any of that would hurt. It is one file.

### Alerts

NetCoreAI watches the three things that go wrong quietly: free disk running out, a model that will not
load, and a run failure rate that has climbed. None of them stops the host, all of them are noticed late,
and the first sign is usually somebody saying it has been broken since Tuesday.

```jsonc
"NetCoreAI": {
  "Alerts": {
    "Enabled": true,
    "WebhookUrl": "https://hooks.example.com/netcoreai",
    "CheckInterval": "00:05:00",
    "ResendAfter": "06:00:00",
    "ErrorRatePercent": 25,
    "ErrorRateMinimumRuns": 20
  }
}
```

Alerts always go to the log, at a level matching their severity, so a host that has configured nowhere to
send them still finds them where it is already looking. A webhook receives each one as JSON. For anything
else — your mailer, your incident tool, a chat channel — wire a sink:

```csharp
builder.Services.AddNetCoreAI()
    .AddAlertSink((alert, ct) => email.SendAsync("ops@example.com", alert.Title, alert.Detail ?? "", ct));
```

There is no `IEmailSender` integration on purpose: that interface lives in ASP.NET Core Identity, and
taking a dependency on Identity to reach it would put it in every host that references NetCoreAI.

Most of the work here is declining to mention things twice. **The same condition alerts once per
`ResendAfter`** — a full disk is still full a minute later, and an alert that arrives every minute is one
nobody reads. **An error rate is ignored until `ErrorRateMinimumRuns` have happened**, because one failed
run out of one is 100% and paging somebody for it is how alerting gets switched off. The rate is measured
over twice the check interval, so a spike straddling two checks is not missed.

`POST /api/alerts/test` sends a test alert, which is never suppressed as a repeat — the one alert that
must always arrive is the one you sent to find out whether they arrive. `GET /api/alerts` lists what has
been raised recently, whether or not a sink took it, so "why did nobody tell me" can be answered with "we
did, at 04:12" rather than with a guess about the webhook.

Webhook traffic goes through the same egress policy as everything else, so a host running under
[data residency](#air-gapped-and-restricted-networks) will not find its alerts leaving.

### Usage and run history

The **Usage** page shows what has been run over a period — runs, tokens, estimated cost, median run time,
failures — broken down by agent, by model and by person, with a filterable browser of the runs themselves
underneath and a CSV export. Also at `GET /api/usage`, `/api/usage/runs` and `/api/usage/export`.

It reads the run traces that were being written anyway. There is no separate accounting table: a second
copy kept for reporting disagrees with the traces the first time a run is written by a path that forgot to
update it. The numbers here are the same rows the run's own trace shows.

Four things worth knowing before you quote a figure from it:

- **A run whose provider reported no usage is counted separately, not as zero.** The page says how many,
  and the totals are understated by that much. A report that hides them reads as precise when it is not.
- **Run time is a median, not a mean.** One thirty-second run should not move the number people quote.
- **Cost is an estimate** from the per-token price on the connection, and is stored as a float for
  summing. The authoritative per-run figure is in the trace.
- **Free-text search covers the page you are on**, not the whole history — the question and the answer
  live inside each trace rather than in a column, and searching all of them properly wants a full-text
  index. Narrow by agent and period first.

How far back it goes is `Storage.RunRetentionDays` (90 by default). The CSV export is capped at 500 runs
per download and prefixes any field starting with `=`, `+`, `-` or `@` with a quote, so an agent id
somebody chose cannot become a formula in whoever opens it.

### The audit log

Who created, changed or deleted a model, connection, tool, agent, knowledge base or API key, and when. On
the **Audit log** page, or `GET /api/audit`. It is append-only: nothing writes over an entry, and the only
way a row leaves is retention.

```jsonc
"NetCoreAI": {
  "Audit":   { "Enabled": true, "RetentionDays": 365, "IncludeRuns": false },
  "Storage": { "RunRetentionDays": 90 }
}
```

`IncludeRuns` is off because every run already writes a trace, and recording both doubles the busiest write
path to say the same thing twice. A guardrail **refusal** is recorded either way — a refusal is not a run,
it is somebody being told no, and that is what an audit log is for.

Three things worth knowing about what is in it:

- **Names are copied at the time**, not looked up later. Half the point is explaining something that has
  since been deleted, and "agent 7f3a… was deleted" answers nobody's question.
- **Secrets are never recorded** — not an API key's secret, not its hash, not a connection's credential.
  Changes to one are recorded as "secret replaced", because a diff of a secret is a copy of it.
- **An API key is distinguished from a person.** "The billing service deleted it" and "someone deleted it"
  lead to different next questions.

A write that fails is logged and swallowed rather than failing the operation it was recording. That is a
deliberate trade: a missing entry is a gap you can see, and an audit log that can fail a save is one that
gets switched off the first time it does. Set `RetentionDays` to 0 to keep entries forever — worth doing
deliberately, since this is the one table nobody notices growing.

Both retention settings are applied by a daily sweep, which also prunes run traces. Traces carry whatever
people asked an agent, which is both the reason to keep them and the reason not to keep them indefinitely.

You can write your own entries through `IAuditLog` — a host's own actions belong in the same log as
NetCoreAI's.

### Watching disk

The Storage page (and `GET /api/storage`) reports what each local model costs, what the data directory totals, and how much room is left on the volume. Remote models cost nothing locally and are left out. A model whose files have gone missing is listed as such rather than quietly dropped, which is the usual sign that a data directory moved between deployments.

Set `Models.StorageQuotaWarningBytes` to be warned before the disk fills; the page also warns when free space drops below 1 GB or a tenth of what the data directory already uses, whichever is larger.

### Reclaiming space

`GET /api/storage/orphans` lists files under `models/` that no registered model claims — leftovers from a removed model, a manual copy, or an interrupted download (`.part` files, which can also be resumed from the Downloads panel instead). `POST /api/storage/orphans/delete` removes the ones you choose.

Deletion is guarded twice, because this is the one place in NetCoreAI where a UI action removes files: a path outside the data directory is refused, and so is any path a registered model claims — including files inside a registered ONNX export folder — even if the caller's list has gone stale. To delete a model's weights, remove the model itself with `deleteFiles=true`.

## Observability

Every generation is counted as it passes through the client pipeline, outside the provider so a failure is counted too, and inside function invocation so one turn with three tool calls counts as one generation. The `NetCoreAI` meter carries `netcoreai.requests` (tagged with the outcome), `netcoreai.errors`, `netcoreai.request.duration`, `netcoreai.tokens.input` / `.output`, `netcoreai.models.loaded` and `netcoreai.generations.active`; subscribe to it with OpenTelemetry for anything durable.

The same events feed an in-process rolling window that the overview page reads for requests per minute, error rate, median latency and per-model traffic. It is bounded, lossy and resets with the host by design — it answers "what is happening right now" without requiring a metrics backend, and is not a substitute for one.

Cost is priced per call from the `CostPer1KInputTokens` / `CostPer1KOutputTokens` recorded on a model's provider connection, and shown per message in the playground. Local models report no cost rather than zero: their price is electricity, which NetCoreAI is in no position to know.

Logging categories start with `NetCoreAI.`. Traces and metrics come from the `NetCoreAI` activity source and meter:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("NetCoreAI"))
    .WithMetrics(m => m.AddMeter("NetCoreAI"));
```

Prompt and completion text stay out of traces unless you set `o.Telemetry.EnableSensitiveData = true`.

## Air-gapped and restricted networks

`o.Network.OfflineMode = true` is also the **data-residency switch**: nothing leaves the process except to
loopback and the hosts named in `o.Network.AllowedHosts`.

```jsonc
"NetCoreAI": {
  "Network": {
    "OfflineMode": true,
    "AllowedHosts": ["mirror.internal", "*.gateway.example.com"],
    "HuggingFaceEndpoint": "https://mirror.internal/hf",
    "ProxyUrl": "http://proxy.internal:3128"
  }
}
```

Covered: Hub browsing, model downloads, **tool invocation**, the **built-in fetch tool**, and remote
provider connections. The first two were the only ones enforced before; a tool is a URL a model can reach,
which makes it a way out of the process like any other.

Three rules worth knowing:

- **Loopback is always allowed.** This machine is not somewhere else — a local Ollama, a sidecar, an
  endpoint tool calling the host's own routes. Refusing them would make the switch unusable exactly where
  it is most wanted.
- **`*.example.com` covers subdomains and nothing that merely resembles one.** `mirror.example.com` yes,
  `evil-example.com` no. A plain suffix match is the usual way an allow-list turns out to allow everything.
- **A remote provider is refused when its connection is resolved**, before any request is built — so a
  provider package that brings its own `HttpClient` is covered without needing the handler.

The fetch tool has its own allow-list as well, and a fetch has to satisfy both: the tool's says where a
model may look, the host's says where this process may talk at all. A refusal comes back to the model as a
sentence rather than an exception, because a tool that throws ends the turn.

Proxy and Hugging Face mirror endpoints are configurable in settings, and both are subject to the same
allow-list — pointing `HuggingFaceEndpoint` at a mirror you have not allowed simply fails.
