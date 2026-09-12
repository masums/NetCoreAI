# NetCoreAI.Dashboard

Embedded management dashboard and API mounted at a configurable path (default /netcoreai). Server-rendered Razor components plus plain JS; no static-file middleware or Blazor circuit required in the host.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
