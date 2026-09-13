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

### Watching disk

The Storage page (and `GET /api/storage`) reports what each local model costs, what the data directory totals, and how much room is left on the volume. Remote models cost nothing locally and are left out. A model whose files have gone missing is listed as such rather than quietly dropped, which is the usual sign that a data directory moved between deployments.

Set `Models.StorageQuotaWarningBytes` to be warned before the disk fills; the page also warns when free space drops below 1 GB or a tenth of what the data directory already uses, whichever is larger.

### Reclaiming space

`GET /api/storage/orphans` lists files under `models/` that no registered model claims — leftovers from a removed model, a manual copy, or an interrupted download (`.part` files, which can also be resumed from the Downloads panel instead). `POST /api/storage/orphans/delete` removes the ones you choose.

Deletion is guarded twice, because this is the one place in NetCoreAI where a UI action removes files: a path outside the data directory is refused, and so is any path a registered model claims — including files inside a registered ONNX export folder — even if the caller's list has gone stale. To delete a model's weights, remove the model itself with `deleteFiles=true`.

## Observability

Logging categories start with `NetCoreAI.`. Traces and metrics come from the `NetCoreAI` activity source and meter:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("NetCoreAI"))
    .WithMetrics(m => m.AddMeter("NetCoreAI"));
```

Prompt and completion text stay out of traces unless you set `o.Telemetry.EnableSensitiveData = true`.

## Air-gapped and restricted networks

`o.Network.OfflineMode = true` blocks Hub browsing, downloads and remote providers, except loopback and hosts listed in `o.Network.AllowedHosts`. Proxy and Hugging Face mirror endpoints are configurable in settings.
