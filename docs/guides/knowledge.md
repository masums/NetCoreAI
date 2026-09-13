# Knowledge bases

Documents your models can answer from, with citations that name the page a passage came from.

## The shape of it

```
data source  ->  extract  ->  chunk  ->  embed  ->  vector store
 files / sql / rest / host push        the base's embedding model
                                                        |
                       retrieval: cosine top-k, threshold, metadata and ACL filters
                                                        |
                                RagChatClient: passages in front of the model, citations out
```

```csharp
var knowledge = app.Services.GetRequiredService<IKnowledgeClient>();
await knowledge.IngestTextAsync("handbook", "policy-1", "Holiday policy", "Everyone gets 25 days.");
var hits = await knowledge.SearchAsync("handbook", "how much holiday do I get?");
```

## Creating a base

A base needs an embedding model, and only models that advertise the `Embeddings` capability are offered. The model is fixed once anything is indexed: vectors from two models are not comparable, so switching is refused with an explanation rather than silently returning nonsense from a mixed index. To change it, delete the documents or make a new base.

`Chunking` decides what retrieval can return. `RecursiveStructure` is the default: headings and paragraphs are kept whole where they fit, and only an oversized paragraph falls back to sentences. `Sentence` never cuts mid-sentence, which matters when a passage is quoted back to a user. `FixedSize` is a predictable token window. `Row` keeps one chunk per row for tabular sources.

## Data sources

| Type | What it reads |
|---|---|
| `files` | The base's upload folder, or any folder the server can see (read-only, never written to) |
| `sql` | Rows from a query, one document per row |
| `rest` | A JSON endpoint, mapped to documents through dotted paths |
| host push | `IKnowledgeClient.IngestAsync` from your own code |

A source is tested before it is saved: a wrong folder or a broken query is far cheaper to find then than two hundred documents into an ingest.

### SQL sources

The query is written by an administrator in the dashboard and runs verbatim — it is a reporting-style read, with nothing to parameterise. Name an id column so a re-sync updates rows rather than duplicating them, and optionally a column whose value becomes an access tag (a tenant or a department), and one carrying a last-modified timestamp.

NetCoreAI ships no database driver beyond SQLite, so the host registers its own in `Program.cs`:

```csharp
DbProviderFactories.RegisterFactory("Npgsql", Npgsql.NpgsqlFactory.Instance);
```

The source then names `Npgsql` as its provider. Connection strings are stored encrypted with Data Protection; because those keys are per-deployment, a database restored onto a new host needs its sources re-entered.

### Scheduling

A source with a cron expression syncs on that schedule — five fields, or six to include seconds. Due times are measured from the source's last run, not from now, so a slot missed while the host was down is picked up when it comes back instead of being skipped. A source that has never synced is measured from when it was created, so restarting a host does not re-crawl everything.

## Ingestion

Syncs run as background jobs. One document that fails is recorded against the job and the rest carry on — a single corrupt PDF must not abandon a five-hundred-file run — and the failures are listed per item on the Knowledge page. A source that fails entirely is recorded against that source without stopping the others.

Documents are skipped when their content hash is unchanged, so a nightly sync costs one extraction per document and no embeddings. When content *has* changed, the old chunks are deleted before the new ones are written: a paragraph removed from a document must stop being retrievable.

## Retrieval and access

Retrieval embeds the query with the base's own model, takes the nearest chunks, and filters them by metadata and by what the caller may see.

Access tags are a set intersection rather than a policy evaluation per chunk. A chunk with **no tags is public**; a chunk with tags is visible to a caller holding any of them, or to one holding `*`. At the HTTP boundary a caller's tags come from their claims, and an unauthenticated caller gets an empty list — never null. Null means "no filtering at all", which is correct for system callers and would be a serious mistake as an endpoint default.

```csharp
// System call: sees everything.
await retriever.SearchAsync(kb, query);

// On behalf of a user: sees public documents plus what their tags allow.
await retriever.SearchAsync(kb, query, callerTags: ["role:hr", "tenant:acme"]);
```

## Answering with citations

`IRagChatClientFactory` wraps a chat client so a turn is answered from the base:

```csharp
var chat = rag.Create("default", new RagOptions { KnowledgeBaseIds = ["handbook"], CallerTags = tags });
var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, question)]);
var citations = response.Messages[^1].Contents.OfType<CitationContent>().SelectMany(c => c.Citations);
```

Passages go in as numbered sources, and the model is told to cite them as `[1]`, `[2]`. Citations stream before the first token, so the UI can show sources beside an answer as it is written.

When retrieval finds nothing, the model is told to say so rather than answer from memory. A confident answer with no sources is the worst thing a RAG system can produce, and it is exactly what a naive implementation does on a miss. Pass `AnswerWithoutContext` to opt out. If retrieval itself fails, the turn is still answered — ungrounded, with no citations, which is the signal.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET/POST | `/api/kb` | List or create knowledge bases |
| GET/PUT/DELETE | `/api/kb/{id}` | One base, with its sources and documents |
| GET/POST | `/api/kb/{id}/sources` | List or save data sources |
| POST | `/api/kb/{id}/sources/test` | Check a source's configuration before saving it |
| POST | `/api/kb/{id}/ingest` | Queue a sync; returns the job |
| GET/POST/DELETE | `/api/kb/{id}/documents` | List, push text, or remove a document |
| POST | `/api/kb/{id}/search` | Search, filtered by the caller's claims |
| GET | `/api/kb/{id}/jobs` | Ingestion history for this base |
| GET | `/api/jobs`, `/api/jobs/{id}` | Background jobs, with per-item failures |
| POST | `/api/jobs/{id}/cancel`, `/retry` | Stop or re-run a job |
