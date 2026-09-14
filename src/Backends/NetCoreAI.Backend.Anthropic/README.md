# NetCoreAI.Backend.Anthropic

Anthropic Messages API (Claude) and Anthropic-compatible proxies via the official Anthropic SDK.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI().AddAnthropicBackend();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
