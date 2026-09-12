# NetCoreAI.Backend.OpenAICompatible

OpenAI, Azure OpenAI and any OpenAI-compatible server (vLLM, LM Studio, Groq, DeepSeek, OpenRouter, Together, Mistral, custom) with presets and encrypted keys.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI().AddOpenAICompatibleBackend();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
