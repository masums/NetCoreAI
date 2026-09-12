# NetCoreAI.VectorStore.Sqlite

Zero-config vector store on SQLite (float32 BLOBs, SIMD brute-force cosine search, ACL and metadata filters).

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI().AddSqliteVectorStore();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
