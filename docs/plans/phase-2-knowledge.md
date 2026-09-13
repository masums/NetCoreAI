# Phase 2 — Knowledge (RAG)

**Goal:** knowledge bases over files, SQL tables, REST endpoints and host-pushed documents, with citation-backed chat and per-user ACL filtering.
**Exit criterion:** chat over a 500-page PDF set (about 10 documents) answers with correct page-level citations; ingestion ≥ 50 pages/min on CPU embeddings.

## Architecture
```
DataSource (IDataSource) --> IngestionPipeline --> IVectorStore
  File / Sql / RestApi / Host(IKnowledgeSource)   extract -> clean -> chunk -> embed -> store
                                                  IDocumentExtractor  IChunker  IEmbeddingGenerator (alias "embed")
Retriever (IRetriever): vector top-k + threshold + metadata filter + ACL filter (caller claims)
KnowledgeClient (IKnowledgeClient): IngestAsync / SearchAsync / DeleteAsync, in-process or HTTP
```
Abstractions added: `IVectorStore` (`UpsertAsync`, `SearchAsync(vector, k, filter)`, `DeleteByDocumentAsync`, `EnsureCollectionAsync(dim, metric)`), `VectorRecord`, `IDocumentExtractor` (`CanHandle(contentType/extension)`, `ExtractAsync` → `ExtractedDocument{Sections[page, heading, text]}`), `IChunker`, `IDataSource` + `IDataSourceFactory`, `IKnowledgeSource` (host push), `IKnowledgeClient`, `IBackgroundJobRunner`, `Citation`, `RetrievalOptions`, `AclTag`.

## Work packages
### WP2.1 Vector store abstraction + SQLite implementation — done
`IVectorStore`; `VectorStore.Sqlite` using `Microsoft.Data.Sqlite` with a pure-.NET brute-force cosine search over a float BLOB column as the zero-config baseline (fine at KB sizes up to roughly 200k chunks), plus optional `sqlite-vec` extension loading when the native library is present. One collection per KB; metadata as JSON column with indexed `document_id` and `acl_tags`. `VectorStoreConformanceTests` (upsert/search/delete/filter/ACL/dimension mismatch). Add metadata tables `KnowledgeBases`, `DataSources`, `Documents`, `Jobs` to Storage.

### WP2.2 Ingestion pipeline core — done
`IngestionPipeline` composed from DI-registered stages; `IBackgroundJobRunner` (channel-based, persisted `Jobs` rows, progress %, retry with backoff, failure log, cancellation, survives restart by re-queueing `Running` jobs at startup); content-hash dedup (`Documents.ContentHash`), re-index only changed docs; chunkers: `FixedSizeChunker` (tokens + overlap, tokenizer from `Microsoft.ML.Tokenizers`), `RecursiveStructureChunker` (headings → paragraphs → sentences), `SentenceChunker`, `RowChunker`; chunk metadata record. Unit tests with golden files.

### WP2.3 Extractors (file upload source) — extractors done; FileDataSource moves to WP2.4 with the other sources
`IDocumentExtractor` implementations: PDF (PdfPig), DOCX/PPTX/XLSX (Open XML SDK), TXT/MD, HTML (AngleSharp → text with heading structure), CSV/JSON (row/object per document). `FileDataSource` with chunked upload endpoint, stored under `{DataDirectory}/kb/{kbId}/files`. OCR deferred to P1.

### WP2.4 SQL and REST data sources — file, REST and host-push sources done; SQL and cron scheduling outstanding
`SqlDataSource`: connection string or host-registered `DbContext`, table/view/custom query, column mapping (id, title, content, metadata[], acl), scheduled sync (cron via `Cronos`), change detection by row hash or `rowversion`/updated-at column. `RestApiDataSource`: URL + headers (secrets encrypted), JSON-path → documents, schedule; Phase 3 dependency handled by implementing it as a plain HTTP fetch now and re-pointing it at Tool definitions when §7.6 lands. `HostKnowledgeSource`: `IKnowledgeSource` / `IKnowledgeClient.IngestAsync` push API.

### WP2.5 Retrieval + ACL
`Retriever`: embed query with the KB's embedding model alias, cosine top-k, threshold, metadata filter DSL (`eq/in/range`), ACL filter = chunk `acl_tags` ⊆ caller claims (tag format `claimType:value`, `*` public). `POST /api/kb/{id}/search` returns chunks with scores and `Citation`s.

### WP2.6 Document chat panel + `IKnowledgeClient`
Dashboard page: pick KBs, chat with citations inline `[1]` → expandable source/page/snippet, "show retrieved chunks" debug drawer with scores, per-session retrieval settings. `RagChatClient` decorator that prepends retrieved context and emits `CitationContent` (custom `AIContent`) so agents in Phase 3 reuse it. `NetCoreAI.Client` gains `KnowledgeClient` (in-process + HTTP via `HttpKnowledgeClient`).

### WP2.7 Management UI + endpoints
KB CRUD pages (embedding model picker limited to `Embeddings` capability, chunking config, vector store selector, access policy), data source pages, jobs page (progress, cancel, retry, failure log). Endpoints per TASKS.md.

### WP2.8 Acceptance run
Fixture: 500 pages of public-domain PDFs + a QA sheet of 30 questions with expected page citations; integration test asserts ≥ 80 % citation hit rate with a GGUF embedding model (nomic-embed-text), nightly job.

## Risks
PDF extraction quality (scanned docs) → OCR in P1; embedding throughput on CPU → batch embedding + parallel stage; SQLite brute-force scaling → sqlite-vec / Postgres in Phase 4.
