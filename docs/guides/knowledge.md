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

A base's name, chunking and default retrieval settings stay editable under **Settings** on its detail panel. Chunking applies to documents ingested from then on; re-ingest a document to re-chunk it.

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

### Tuning what gets retrieved

The chat page's Retrieval panel sets how many passages a question pulls and the score below which a passage is dropped, for that chat only. Ticking **Show retrieved chunks** puts every retrieved passage under the answer with its score and its whole text — including the ones the model ignored, which is the point: a chunk that should have been retrieved and was not is visible by its absence, and a chunk cut through the middle of the answer is visible by where it starts.

Programmatically that is `RagOptions.IncludeRetrievedPassages`, which attaches a `RetrievedContext` alongside the citations. It is off by default because whole passages are far larger than the answer they produced, and it is not persisted with the message: a tuning aid belongs to the turn that asked for it.

When retrieval finds nothing, the model is told to say so rather than answer from memory. A confident answer with no sources is the worst thing a RAG system can produce, and it is exactly what a naive implementation does on a miss. Pass `AnswerWithoutContext` to opt out. If retrieval itself fails, the turn is still answered — ungrounded, with no citations, which is the signal.

## From a separate application

`IKnowledgeClient` has two implementations and one shape. In the host, `AddNetCoreAI()` registers the in-process one. In another application, point the client at the host:

```csharp
builder.Services.AddNetCoreAIClient(o =>
{
    o.BaseUrl = new Uri("https://myapp.example.com/netcoreai");
    o.ApiKey = builder.Configuration["NetCoreAI:ApiKey"];
});
```

The same calls then work, over HTTP. Two differences are inherent to the wire rather than accidental. Search results arrive without their embedding vectors, because sending a few hundred floats per hit would dwarf the passage itself and no caller has a use for them. And `callerTags` cannot be set from a client: a client declaring its own access tags could read every restricted passage by asking for `*`, so the host derives them from the API key or signed-in user and the client refuses the argument rather than ignoring it.

## Uploading documents

`POST /api/kb/{id}/documents/upload?fileName=handbook.pdf` takes the file as the request body. The file is written into the base's upload folder and indexed immediately, under the id the folder's own file source would give it — so an upload and a later folder sync are one document, not two. Uploading the same name again is an edit: the old chunks go before the new ones arrive.

A base's first upload creates its `Uploads` file source if it has none, so the folder has exactly one owner. The name is sanitised down to its last path segment; a browser is free to send `../../appsettings.json`, and only `appsettings.json` survives that.

On the Knowledge page the same thing is **Upload files** under a base's documents — one request per file, so a document that fails is named rather than taking the others down with it.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET/POST | `/api/kb` | List or create knowledge bases |
| GET/PUT/DELETE | `/api/kb/{id}` | One base, with its sources and documents |
| GET/POST | `/api/kb/{id}/sources` | List or save data sources |
| POST | `/api/kb/{id}/sources/test` | Check a source's configuration before saving it |
| POST | `/api/kb/{id}/ingest` | Queue a sync; returns the job |
| GET/POST/DELETE | `/api/kb/{id}/documents` | List, push text, or remove a document |
| POST | `/api/kb/{id}/documents/upload` | Upload a file as the request body and index it |
| POST | `/api/kb/{id}/search` | Search, filtered by the caller's claims |
| GET | `/api/kb/{id}/jobs` | Ingestion history for this base |
| GET | `/api/jobs`, `/api/jobs/{id}` | Background jobs, with per-item failures |
| POST | `/api/jobs/{id}/cancel`, `/retry` | Stop or re-run a job |

## Hybrid search

Retrieval runs a vector search and a keyword search and fuses the two. This is the default, and there is a
`Retrieval.Mode` of `Vector` or `Keyword` if you want only one.

The two fail differently, which is the whole argument for having both:

- A **vector** search finds text that *means* the same thing, and misses `ERR-4021` — nothing else means
  the same as an error code. Or a part number, or a customer id.
- A **keyword** search finds exact tokens, and misses "the login screen hangs" when the document says
  "authentication times out".

A store that cannot search by keyword falls back to vectors alone. Hybrid is a default, and a default has
to work wherever it lands.

### How the two are combined

Reciprocal rank fusion: each passage scores `1/(60 + rank)` in each list it appears in, and the scores add
up.

**On rank, not on score.** A cosine similarity and a BM25 score are different things measured differently;
normalising one onto the other invents a relationship that is not there. Rank is the only thing the two
lists agree about.

The effect is that a passage both searches liked moderately beats one that only a single search loved —
and a passage only one search found still reaches the answer, further down. Each leg fetches three times
the requested depth, because a passage ranked eighth by one and second by the other is exactly the one
fusion exists to surface.

`MinScore` applies to the vector leg, before fusion. A fused score is a rank sum rather than a similarity,
and a threshold tuned against cosine distances means nothing against it.

### The keyword index

In SQLite it is FTS5 over the chunk text that is already stored — no second copy — kept in step by database
triggers rather than by code that has to remember to update it. An index maintained by whichever path
remembers is wrong after the first path that forgets.

It applies the same access tags as the vector search. An index that ignored them would be a way to read a
restricted passage by guessing a word in it.

Queries are tokenised the way the index is: `AB-1234/X` becomes the phrase `"AB 1234 X"` rather than one
glued token, because that is how the document was stored. Every term is quoted — partly so punctuation
cannot be read as an FTS5 operator, and partly because a query is text somebody typed and must never become
part of the expression evaluating it.

## Re-ranking

Hybrid search gets the right passages into the candidate set. A re-ranker decides which of them goes first
— and the order is what decides the answer, because a model given ten passages leans on the first two. A
correct passage ranked seventh is, in practice, a passage that was not retrieved.

```csharp
builder.Services.AddNetCoreAI()
    .AddOnnxBackend()
    .AddOnnxReranker("./models/bge-reranker-base");
```

Nothing else changes. Every knowledge base re-ranks by default once a re-ranker is registered, and none
does while none is — a host that adds one gets the benefit without finding a setting first, and a host that
does not pays nothing.

**Why a second pass at all.** A vector search compares a question and a passage that were embedded
separately and never saw each other. A cross-encoder reads the two *together* and answers one question:
does this passage answer that one. It is far better at it, and far too slow to run over a corpus — which is
why it goes second, over a few dozen candidates something cheap has already found.

Retrieval fetches wider than the answer when a re-ranker will read the results — `RerankCandidates`,
30 by default. The whole value is in the passages the first stage ranked eighth, so the pool has to be
bigger than the answer; too big and every question pays for passages that were never plausible.

A re-ranker that fails costs quality, not the answer. The candidates were already a reasonable result, and
the retrieval order is used with a warning logged. Turning a quality feature into an availability one would
be the wrong trade.

Set `Retrieval.Rerank = false` on a knowledge base that should skip it.

**Not yet tested end to end.** The seam is covered — the wider net, the re-ordering, the cut, the failure
path — but whether `bge-reranker-base` itself ranks well is checked by hand rather than by a test, because
that needs a model download. Treat the ONNX implementation as newer than the rest of this page.
