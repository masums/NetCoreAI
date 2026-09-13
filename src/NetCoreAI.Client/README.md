# NetCoreAI.Client

Typed client for NetCoreAI hosts. Usable from any .NET app.

```csharp
builder.Services.AddNetCoreAIClient(o =>
{
    o.BaseUrl = new Uri("https://myapp.example.com/netcoreai");
    o.ApiKey = builder.Configuration["NetCoreAI:ApiKey"];
});
```

That registers `IKnowledgeClient` against the remote host. It is the same interface `AddNetCoreAI()` registers in-process, so moving code between the two is a change of registration and nothing else. `IAgentClient` follows in Phase 3.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
