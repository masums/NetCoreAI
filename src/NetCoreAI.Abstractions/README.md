# NetCoreAI.Abstractions

Interfaces and records only (IModelProvider, IChatClientFactory, IModelRegistry, IVectorStore, IMetadataStore...). Reference this from libraries that want to plug into NetCoreAI without taking a dependency on the implementation.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
