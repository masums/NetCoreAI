# Getting started

> Status: Phase 1 in progress. Remote providers (Ollama, OpenAI-compatible, Anthropic), the model registry, the chat playground and the dashboard work today. Local GGUF/ONNX execution and the Model Hub land in the next work packages.

## 1. Install

```bash
dotnet add package NetCoreAI
dotnet add package NetCoreAI.Backend.Ollama              # or .OpenAICompatible / .Anthropic
```

## 2. Register and map

```csharp
builder.Services.AddNetCoreAI(o =>
{
    o.DataDirectory = "./netcoreai";
    o.Dashboard.Authorization = p => p.RequireRole("Admin");   // default is deny-all
})
.AddOllamaBackend()
.AddOpenAICompatibleBackend()
.AddAnthropicBackend();

app.MapNetCoreAI();
```

`AddNetCoreAI()` is idempotent and registers the SQLite metadata store and SQLite vector store automatically when you reference the `NetCoreAI` meta-package. `MapNetCoreAI()` adds one endpoint group under `/netcoreai`; nothing outside that prefix changes.

## 3. Connect a model

Open `/netcoreai/providers`, pick a provider and preset, paste the key and save. The connection is tested and its models are synced into the registry in one step. The first chat-capable model becomes the `default` alias, the first embedding model becomes `embed`.

API keys are encrypted with ASP.NET Core Data Protection and never returned to the browser. In containers, override any key without touching the database:

```
NETCOREAI__CONNECTIONS__OPENAI_PROD__SECRET=sk-...
```

The middle segment is the connection name with spaces replaced by underscores, or its id.

## 4. Chat

`/netcoreai/chat` streams responses over Server-Sent Events, persists conversations, and supports stop, regenerate, edit-and-resend and export to Markdown or JSON.

## 5. Use it from your own code

```csharp
public class InvoiceSummarizer(IChatClient chat)          // the "default" alias
{
    public async Task<string> SummarizeAsync(string text)
        => (await chat.GetResponseAsync($"Summarize:\n{text}")).Text;
}

public class Router(IChatClientFactory models)             // any alias or model id
{
    public Task<ChatResponse> FastAsync(string prompt) => models.Get("fast").GetResponseAsync(prompt);
}
```

Aliases carry an ordered fallback list, so `Get("quality")` can try a local model, then Ollama, then Claude, failing over on provider errors, timeouts and rate limits.

## 6. Settings

`/netcoreai/settings` writes to the metadata store and takes effect immediately; environment variables still win. It covers the data directory, context size, idle unload, memory budget, execution provider, threads, concurrency, offline mode, proxy, download bandwidth limit, the Hugging Face endpoint and token, and which providers are enabled.

### Hugging Face API token

Set it under **Network & Hugging Face**. It is stored encrypted and sent as a bearer token on every Hub request. A token is optional but recommended: it raises rate limits, speeds up browsing and downloads, and is required for gated repositories. Create one at <https://huggingface.co/settings/tokens> with read scope. Type `clear` into the field to remove a stored token. In containers use `NETCOREAI__NETWORK__HUGGINGFACETOKEN`.

## 7. Health

`/netcoreai/health` sits outside the authorization policy so load balancers can probe it. It reports readiness, loaded models, metadata-store connectivity and free disk space.
