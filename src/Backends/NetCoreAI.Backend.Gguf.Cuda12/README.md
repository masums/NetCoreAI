# NetCoreAI.Backend.Gguf.Cuda12

NVIDIA CUDA 12 native backend for NetCoreAI.Backend.Gguf. Add this package alongside NetCoreAI.Backend.Gguf and set the execution provider to Cuda.

```csharp
builder.Services.AddNetCoreAI().AddGgufBackend();
```

Then choose the execution provider in the NetCoreAI dashboard under Settings > Execution. llama.cpp selects its backend once per process, so a change takes effect after a restart.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI).
