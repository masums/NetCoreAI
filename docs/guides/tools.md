# Tools

Things a model can do, rather than only say. A tool is one of your endpoints, a method in your code, or an operation on some other service.

## Four kinds

| Kind | Where it comes from | How it runs |
|---|---|---|
| `Endpoint` | One of this host's routes, found by discovery | HTTP loopback, or in-process if opted in |
| `Code` | A method marked `[AITool]` | In the host's process, as the host |
| `OpenApi` | An imported OpenAPI 3.x document | HTTP, to that service |
| `Manual` | Written by hand: a URL template and parameters | HTTP |

## From an endpoint you already have

Discovery reads the route table and lists everything the host serves, with each endpoint's parameters and the authorization it declares. Nothing is exposed by being listed: a tool exists when somebody saves one.

```csharp
app.MapGet("/api/orders/{id}", (string id, IOrderStore store) => store.FindAsync(id))
   .WithAITool("get_order", "Look up one order by its id.");
```

`.WithAITool()` (or `[AIToolEndpoint]` on a method) says two things: this endpoint is meant to be a tool, and it may run in-process. Neither weakens its authorization — a tool call runs as the caller, so an endpoint that refuses them refuses the tool.

Endpoints nobody marked are still listed, and an administrator can still make tools of them. Endpoints that *cannot* be tools — a file upload, say — are listed with the reason rather than hidden, because a missing endpoint is harder to debug than a disqualified one.

### The two invocation modes

**HTTP loopback** is the default. The host calls itself, so the request goes through the real middleware pipeline exactly as a browser's would. It needs to know its own address; set `NetCoreAI:Tools:BaseAddress` when the host is behind a proxy and cannot work it out.

**In-process** runs the endpoint without a socket, with a synthetic request carrying the caller. It is faster and needs no address, and it is **opt-in per endpoint** — by attribute, or by an administrator enabling it in the designer, which is recorded on the tool and logged with who did it.

The difference matters and is worth stating plainly. Invoking an endpoint this way runs the endpoint and its filters, **not** the application's middleware, so NetCoreAI evaluates the endpoint's own authorization itself — the same metadata, the same policy provider, the same `IAuthorizationService` the middleware uses. What is *not* reproduced is middleware unrelated to that authorization: IP allow-lists, forwarded headers, rate limiters, antiforgery. A synthetic request has no connection for those to read. If an endpoint depends on any of them, leave it on loopback. See [ADR-0004](../adr/0004-in-process-tool-invocation.md).

## From your own code

When an endpoint would be ceremony, mark a method:

```csharp
public sealed class MoneyTools(IRates rates)
{
    [AITool("convert_money", Description = "Convert an amount from pounds into another currency.")]
    public decimal Convert(
        [Description("The amount in pounds.")] decimal amount,
        [Description("ISO currency code, such as EUR.")] string currency) =>
        Math.Round(amount * rates.For(currency), 2);
}
```

```csharp
builder.Services.AddNetCoreAI().AddAITool<MoneyTools>();
```

The signature is the schema: parameter names, types and `[Description]` attributes are what the model is shown, and those descriptions are most of what makes a tool call accurate. Instance methods get their dependencies from DI. A code tool is read-only in the designer — editing a row would be editing a copy of something the compiler owns; change the method instead.

A code tool runs **as the host**, not as the caller, because there is no endpoint and so nothing evaluates authorization. Anything a caller should not be able to do indirectly has to check that itself.

## From somebody else's API

```
POST /netcoreai/api/tools/import-openapi   { "document": "…", "baseUrl": "https://staging.internal" }
```

One tool per operation, named from `operationId`. Re-importing an updated document updates the tools it made rather than leaving a second copy of every operation. `baseUrl` overrides the document's own `servers` entry, which is often a placeholder or points at the vendor's production host.

JSON documents only. A YAML one is refused with a message saying to convert it — parsing YAML would mean a parser and its dependencies in the package every host references, for one import feature.

An imported tool never carries the caller's credentials: those are for this host, and forwarding them would hand them to whoever runs the other service. Give it its own credential instead.

## What the model may set, and what it may not

Every parameter is either the model's or the host's.

```csharp
new ToolParameter { Name = "tenantId", Binding = ParameterBinding.Claim, BindingSource = "tenant" }
```

A parameter bound from a claim, from request metadata, or to a static value is **locked**. There are two defences and both matter:

- It is **absent from the schema** the model is shown, so there is no property to fill in.
- The binder consults the model's arguments only for parameters the definition says it may set, so a value it sends anyway is **dropped, not merged**.

A locked parameter whose value cannot be found refuses the call rather than sending null — for a tenant id, null is the difference between "this tenant" and "all of them".

## Safety

`ReadOnly` or `SideEffecting`, with a confirmation policy. A `GET` becomes read-only and everything else side-effecting until someone says otherwise, because the reverse mistake is the expensive one.

An `AdminOnly` tool is **withheld from the list** for callers who are not administrators, rather than offered and refused on use: a model told about a tool will try it, and a refusal mid-turn spends a call and invites it to look for a way round.

## What comes back

A tool's response is cut to `MaxBytes` (16 KB by default) and says when it was cut. A model reading a truncated list as though it were whole answers confidently from half the data. `SelectPath` picks the part worth returning, so a model sees `42` rather than the object it sat in.

A failed call is reported to the model as text, not thrown: "that returned 503" is something it can act on or report, while an exception ends the turn and the person who asked sees nothing.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/tools` | Every tool, saved and code-defined |
| GET | `/api/tools/discover` | Endpoints this host routes, and which already have tools |
| GET/POST/PUT/DELETE | `/api/tools`, `/api/tools/{id}` | Manage saved tools |
| POST | `/api/tools/from-endpoint/{endpointId}` | Build a tool from a discovered endpoint |
| POST | `/api/tools/import-openapi` | Import an OpenAPI 3.x document |
