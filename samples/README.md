# Samples

Each sample is a normal ASP.NET Core app that references `NetCoreAI.*` **as NuGet packages** (see `Directory.Build.props` for the pinned version and `nuget.config` for the local feed).

```bash
# from the repo root: build + pack the framework into artifacts/packages
./build/pack.sh          # or build/pack.ps1
cd samples && dotnet run --project MinimalApi
# open https://localhost:5001/netcoreai
```

| Sample | Shows |
|---|---|
| `MinimalApi` | Smallest host: `AddNetCoreAI()` + `MapNetCoreAI()` + `IChatClientFactory` from an endpoint |
| `MvcExistingApp` (planned, Phase 3) | Existing MVC controllers exposed as tools |
| `BlazorHost` (planned, Phase 4) | Embeddable chat widget |
| `RagDocuments` (planned, Phase 2) | Knowledge base over PDFs + SQL table |
| `DockerGpu` (planned) | docker-compose with the CUDA backend |
