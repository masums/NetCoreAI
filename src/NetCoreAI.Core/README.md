# NetCoreAI.Core

Model registry, provider registry, lifecycle manager, hardware probe and fit estimator, connection manager, settings, chat sessions. Everything host-independent.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
