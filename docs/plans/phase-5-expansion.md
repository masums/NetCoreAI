# Phase 5 — Expansion (P2, roadmap-driven)

Each item is an independent package or feature behind an existing extension point; order is decided by community demand after 1.0.

| Item | Extension point | Notes |
|---|---|---|
| Native Safetensors runtime | `IModelProvider` (`Backend.Safetensors`) | TorchSharp; Llama/Phi/Qwen families first |
| MCP client | `IToolProvider` | consume MCP servers as tool sources (stdio + HTTP) |
| MCP server | new endpoint under `/netcoreai/mcp` | expose tools/agents to MCP hosts |
| Multi-agent | `AgentEngine` | agent-as-tool, handoff patterns |
| Vision input | `ModelCapabilities.Vision` already modelled | chat attachments + agent requests with `DataContent` |
| Data sources | `IDataSource` | folder watch, web crawl (robots), SharePoint / Google Drive / S3 |
| Hub sources | `IModelSource` | Ollama library, ModelScope, private registries |
| Remote providers | `IModelProvider` | Gemini, Bedrock, Azure AI Foundry |
| Retrieval | `IRetriever` | query rewriting, HyDE |
| Memory | KB-backed long-term user memory | per-user KB with automatic summarisation |
| Converters | `IModelConverter` | plugin packages |
