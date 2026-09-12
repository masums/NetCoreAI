# NetCoreAI.Backend.Ollama

Ollama servers (local or LAN) as a peer provider: model listing, chat, embeddings, tools, streaming.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI().AddOllamaBackend();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
