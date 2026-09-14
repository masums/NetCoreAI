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

## Trying one out

The Tools page has a test panel per tool. Give the arguments yourself, or ask a question and let the model choose them — the arguments it picked are shown back, which is the part worth looking at: a tool called with the wrong ones has a description problem, and that is invisible otherwise.

When the model decides *not* to call the tool, that is the answer rather than an error. Most tool problems are not "the call failed" but "the model did not think this tool applied", and its description is what it reads to decide.

The model is offered a stand-in with the same name and schema, so a trial never fires the real tool inside the model's turn and then again for the result — a side-effecting tool must not do its work because somebody typed a sentence into a test box. The call happens once, after the arguments are known.

The trial runs as **you**, not as the host, so a tool you could not use does not appear to work when you try it. Locked parameters are filled from your identity whatever the box says, and the panel names them so the result is not a surprise.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/tools` | Every tool, saved and code-defined |
| GET | `/api/tools/discover` | Endpoints this host routes, and which already have tools |
| GET/POST/PUT/DELETE | `/api/tools`, `/api/tools/{id}` | Manage saved tools |
| POST | `/api/tools/from-endpoint/{endpointId}` | Build a tool from a discovered endpoint |
| POST | `/api/tools/import-openapi` | Import an OpenAPI 3.x document |
| POST | `/api/tools/{id}/test` | Run one trial call, with given or model-chosen arguments |

## Reaching this host from another application

```csharp
builder.Services.AddNetCoreAIClient(o =>
{
    o.BaseUrl = new Uri("https://myapp.example.com/netcoreai");
    o.ApiKey = builder.Configuration["NetCoreAI:ApiKey"];
});
```

That registers `IAgentClient` and `IKnowledgeClient` against the remote host — the same interfaces `AddNetCoreAI()` registers in-process, so the calling code is identical either way.

The host has to accept keys, which is two lines because it changes who can reach your API:

```csharp
builder.Services.AddAuthentication().AddNetCoreAIApiKey();
builder.Services.AddNetCoreAI(o => o.Dashboard.Authorization = p => p
    .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
    .RequireAuthenticatedUser());
```

Keys are made on the API (`POST /netcoreai/api/keys`) and the secret is shown **once**: only a hash is stored, so a stolen database yields no working keys and a lost secret is replaced rather than recovered.

A key carries:

- **Scopes** — which agents it may run and which knowledge bases it may search. Empty means *none*, because the safe reading of "nobody said what this may do" is "nothing". `*` means everything.
- **Claims** — a service identity. These decide which documents it can retrieve and which agents its access tags allow, exactly as a person's claims would, so granting one grants everything that claim grants.
- **A rate limit** per minute, **an allow-list** of addresses or CIDR ranges, and **an expiry**.

The rate limit is counted per key **in each process**. Behind a load balancer, each instance allows the configured rate, so the effective limit is the limit times the number of instances — a deliberate simplification, since a shared counter would mean a shared store that NetCoreAI does not otherwise require.

## Goal G3: an endpoint becomes a tool

The whole of what a developer writes:

```csharp
app.MapGet("/api/orders/{id}", (string id, IOrderStore store) => store.FindAsync(id))
   .WithAITool("get_order", "Look up one customer order by its id, returning its status, total and carrier.");
```

Then, on the Tools page, **Make a tool** on that endpoint; on the Agents page, create an agent and tick it. Or two API calls: `POST /api/tools/from-endpoint/{id}` and `POST /api/agents`.

That path is exercised end to end by `Phase3AcceptanceTests`, against a real model rather than a stub. It asserts what a fake cannot: that a model told nothing about the endpoint beyond that one description **decides to call it**, sends the right id, and answers from what came back rather than from what it imagined. It also asserts that a locked parameter is not filled in by a real model that is actively trying to supply one.

Run it with `OPENROUTER_FREE_KEY` set (and optionally `OPENROUTER_FREE_MODEL`); without a key the tests skip, so the suite still runs offline and in CI without secrets. A provider rate-limit is skipped rather than failed — a free tier refusing a burst is a fact about the tier, not a defect here, and a suite that goes red for it teaches everyone to ignore red.

**What has not been measured:** a stopwatch run on a clean machine, from `dotnet add package` to a working tool, by someone who has not seen this before. The mechanics above are proven; the five-minute claim is not, and saying otherwise would be guessing at the part that actually matters — how long it takes someone to find out that `.WithAITool()` is the thing to type.

## The tools NetCoreAI ships with

```csharp
builder.Services.AddNetCoreAI().AddBuiltInTools(o =>
{
    o.FetchAllowedHosts.Add("docs.example.com");
    o.Sql = new SqlToolConnection("Npgsql", reportingConnectionString) { AllowedTables = ["orders", "customers"] };
});
```

| Tool | What it does | Default |
|---|---|---|
| `current_date_time` | Today's date in UTC, so a model stops guessing it from its training data | on |
| `calculate` | Arithmetic, which language models are unreliable at | on |
| `search_documents` | Searches your knowledge bases, filtered by the caller's access tags | on |
| `fetch_url` | Reads a page from an approved site | **only with an allow-list** |
| `query_database` | Runs a read-only SELECT | **only with a connection** |

The last two are off until you say where they may point, and they are *absent* rather than present-and-refusing — a model told about a tool will try it, spend a call, and read the refusal as a fault.

**`fetch_url`** takes an allow-list of exact host names, never a block-list. A model that can fetch any URL is a request-forgery hole with a friendly name: it sits inside your network, and the addresses worth reaching from there are exactly the ones a block-list forgets — the link-local metadata service that hands out cloud credentials, an admin panel bound to localhost, anything on a private range. Address literals are refused whatever the list says, since a list of names tells you nothing about what an IP currently points at, and redirects are not followed, because a redirect is a second URL the allow-list was never asked about.

**`query_database`** has three limits, because any one alone is thin: the statement must be a single SELECT, the rows are capped, and you should point it at an account that can only read. The string check is the weakest of the three — it refuses a second statement smuggled after a semicolon, and writes hidden inside a SELECT — but a permission the account does not have is what holds when a check is wrong. `AllowedTables` is worth setting even then: "read-only" and "may read the password hashes" are not mutually exclusive.

**`calculate`** parses the expression itself rather than evaluating it. Its input comes from a model, which in practice means from whoever is talking to the model, and an expression evaluator that can reach a type system is a way to run code by asking nicely.
