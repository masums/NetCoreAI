# Model Hub

Browsing, downloading and importing models. Everything here runs on the server: the browser never talks to Hugging Face, so a token stays server-side and offline mode has one place to enforce.

## Browsing

```csharp
var hub = app.Services.GetRequiredService<IHubService>();
var results = await hub.SearchAsync(new ModelSearchQuery { Text = "qwen2.5", Format = ModelFormat.Gguf });
var view = await hub.GetAsync("Qwen/Qwen2.5-0.5B-Instruct-GGUF");
```

Hugging Face is registered by default. Add another source by implementing `IModelSource` and calling `AddModelSource<T>()`; it appears in the same search results and download queue with no other change.

**Variants, not files.** A repository is grouped into the units someone actually downloads: one per GGUF quantization (a split `…-00001-of-00003.gguf` model counts as one variant carrying every part), and one per ONNX Runtime GenAI folder, which travels with its config, external data and tokenizer. Files that no runtime loads — READMEs, loose tokenizers — are never offered on their own.

**Fit badge.** Each variant is sized against this machine before anything is downloaded, using the size and quantization the hub reports through the same estimator the registry uses afterwards. `Fits`, `Tight`, `too big` or `unknown`; the biggest variant that still fits is listed first.

**Gated and missing repositories.** Hugging Face answers `401` both for a repository that does not exist and for one that is private or gated, because it will not confirm a private repo exists. The error names both causes rather than guessing. For gated models, accept the licence on the site and set a token in Settings → Network or the `HF_TOKEN` environment variable.

## Recommended

`GET /api/hub/recommended` serves a curated list fetched from `Network.RecommendedManifestUrl` and cached for twelve hours, with a copy compiled into the package as a fallback, so a first run with no network still has something to offer. Each entry carries a suggested alias, so one click can make a model the `default` chat or `embed` model.

## Downloading

```csharp
var job = await downloads.EnqueueAsync(new DownloadRequest("huggingface", "Qwen/Qwen2.5-0.5B-Instruct-GGUF", ["qwen2.5-0.5b-instruct-q4_k_m.gguf"]));
```

Jobs run one at a time, so a slow connection is not divided between files. Per file:

- **Resumable.** Bytes land in a `.part` file with a JSON sidecar recording the size and validator the transfer started from. A restart, a pause or a dropped connection resumes with a range request; if the remote file has changed since, the partial bytes are discarded rather than spliced onto a different version.
- **Parallel.** Files big enough to be worth it are split into `Network.ParallelDownloadChunks` ranges (at least 8 MB each) written straight to their own offsets. Servers that do not support ranges fall back to one plain GET.
- **Rate limited.** `Network.BandwidthLimitBytesPerSecond` is a token bucket shared by every download, so the cap is on total bandwidth rather than per file.
- **Verified.** The SHA-256 published in the repository's LFS metadata is checked before the file is moved into place. A mismatch deletes the file and says so, rather than leaving a corrupt model to fail hours later at load time.

Downloads are persisted as they change, so whatever was in flight is re-queued after a restart and picks up the bytes already on disk. Pause, resume and cancel are all live; cancelling also removes the partial files.

When a job finishes, the files are identified and registered, which is what makes a downloaded model appear on the Models page ready to load.

## Importing

```csharp
await importer.ImportFromPathAsync("/models/qwen2.5-0.5b-instruct-q4_k_m.gguf");
await importer.ImportFromUrlAsync(new Uri("https://internal-mirror/model.gguf"));
```

A path is read by the server, not the browser: a `.gguf` file, or a folder holding `genai_config.json` or an ONNX export with its tokenizer. Pass `copyIntoDataDirectory: true` to copy it in so deleting the model later stays self-contained. A URL is queued through the same download manager, so it resumes and verifies like any hub file.

## Format detection

Core knows no weight format. Import and download ask the registered backends, each of which implements `IModelFormatDetector` and inspects the file rather than trusting its extension — the GGUF backend reads the header, the ONNX backend reads the folder config. The first that recognises the path wins, and what it reports (architecture, context length, quantization, chat template, capabilities) becomes the registry entry.

A model whose backend is not referenced still downloads; it is logged and left unregistered, naming the package to add.

## Offline and restricted networks

`Network.OfflineMode` refuses every NetCoreAI-initiated outbound call at one delegating handler, so no code path can leak past it, and the hub pages say so rather than failing obscurely. `Network.AllowedHosts` exempts internal mirrors (a leading `*.` matches subdomains). `Network.ProxyUrl` routes both hub browsing and downloads. Importing from disk keeps working throughout.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/hub/sources` | Registered model sources |
| GET | `/api/hub/search` | Search, with `q`, `author`, `task`, `format`, `license`, `sort`, `limit`, `offset`, `source` |
| GET | `/api/hub/recommended` | Curated list with fit verdicts (`refresh=true` bypasses the cache) |
| GET | `/api/hub/models/{owner}/{repo}` | Repository detail, variants and fit |
| GET/POST | `/api/downloads` | List or queue downloads |
| GET/DELETE | `/api/downloads/{id}` | One job; DELETE cancels and discards partial files |
| POST | `/api/downloads/{id}/pause`, `/resume` | Pause and resume |
| POST | `/api/models/import` | Import from a server path or a URL |
