# NetCoreAI.Storage.Sqlite

Default metadata store on SQLite via EF Core: models, aliases, connections, sessions, settings, downloads.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI().AddSqliteStorage();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
