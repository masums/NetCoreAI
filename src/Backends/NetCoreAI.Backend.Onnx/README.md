# NetCoreAI.Backend.Onnx

Runs ONNX Runtime GenAI model folders (genai_config.json) and ONNX embedding models in-process.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI().AddOnnxBackend();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
