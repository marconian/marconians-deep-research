# Marconian Main Development Plan

## Pre-flight & Planning
- [x] Verify .NET 8+ SDK availability (`dotnet --version`)
- [x] Draft initial development plan aligned with docs/specs.md directives (this document)
- [x] Review `docs/user_requests.md` at each working session and update plans/instructions as needed

## M0 - Repository & Project Scaffolding
- [x] Create `src/Marconian.ResearchAgent` console project targeting .NET 8
- [x] Ensure solution file encompasses main project and future test project
- [x] Add required NuGet dependencies to `Marconian.ResearchAgent`
- [ ] Commit baseline scaffolding (deferred until final approval to avoid premature commits)

## M1 - Configuration & Core Services
- [x] Implement `Settings` static class to load environment variables
- [x] Validate required configuration at startup with detailed error reporting
- [x] Create Azure OpenAI service wrapper (client init + request helpers + error handling)
- [x] Create Cosmos DB wrapper for document persistence (init + CRUD helpers + error handling)
- [x] Create Redis cache wrapper leveraging `Microsoft.Extensions.Caching.StackExchangeRedis`

## M2 - Agent & Tool Abstractions
- [x] Define `IAgent` interface with `ExecuteTaskAsync`
- [x] Define `ITool` interface with `Name`, `Description`, and `ExecuteAsync`
- [x] Establish shared models for tool results, agent context, and errors

## M3 - Memory Modules
- [x] Implement short-term memory manager with context window tracking and summarization pipeline via LLM wrapper
- [x] Implement long-term memory manager using Cosmos DB + embeddings via Azure OpenAI
- [x] Implement in-memory cosine similarity search layer over stored embeddings
- [x] Define persistence schema/contracts for memory entries (short-term snapshots & long-term documents)

## M4 - Tool Implementations
- [x] Implement `WebSearchTool` using Google Custom Search API (via HTTP client)
- [x] Implement `WebScraperTool` using `HtmlAgilityPack` and `Microsoft.Playwright`
- [x] Implement `FileReaderTool` leveraging local storage registry + Document Intelligence for PDFs
- [x] Implement `ImageReaderTool` leveraging multimodal Azure OpenAI endpoint & local registry
- [x] Integrate tool logging, retries, and error surfaces to memory layer

## M5 - Agent Implementations
- [x] Implement `ResearcherAgent` with tool orchestration, memory utilization, and reporting contract
- [x] Implement `OrchestratorAgent` state machine (Planning -> Executing_Research_Branches -> Synthesizing_Results -> Generating_Report)
- [x] Implement branching logic for spawning `ResearcherAgent` instances via `Task.Run`
- [x] Integrate agent-tool communication protocols and memory interactions

## M6 - Synthesis & Reporting
- [x] Implement aggregation of researcher outputs with deduplication and confidence scoring
- [x] Implement synthesis pipeline using LLM to generate structured findings summaries
- [x] Generate final Markdown report with citations referencing memory records and tool outputs
- [x] Persist final report + metadata to long-term memory

## M7 - Application Entry Point
- [x] Compose dependency wiring in `Program.cs` (config, services, agents, tools)
- [x] Implement startup validation flow and orchestrator bootstrap with sample query
- [x] Support CLI arguments and interactive session selection backed by Cosmos session metadata
- [x] Add graceful shutdown, logging, and exception handling wrappers

## M8 - Testing & Verification
- [x] Create `tests/Marconian.ResearchAgent.Tests` NUnit project and link to solution
- [x] Add test project dependencies (`NUnit`, `NUnit3TestAdapter`, `Microsoft.NET.Test.Sdk`, `Moq`)
- [x] Write unit tests for memory services, LLM wrapper, and orchestrator state transitions (with mocks)
- [x] Write integration tests for critical tools (e.g., `WebSearchTool` minimal live call)
- [x] Run `dotnet test` and resolve failures
- [x] Execute `dotnet build --configuration Release` and confirm success

## M9 - Enhancements from Latest Requests
- [x] Extend logging coverage by threading `ILogger` through core agents, services, and tools with contextual scopes
- [x] Update Cosmos persistence to operate in gateway mode and provision vector indexing for similarity search
- [x] Document and implement persistent research state schema (sessions, branches, tool telemetry) in Cosmos
- [x] Enhance Markdown report workflow to support iterative editing (targeted line inspection and in-place updates)
- [ ] Conduct a self-driven research run of the app (once dependencies configured) to validate new capabilities and gather insights
- [x] Reflect resulting architectural adjustments in `docs/architectural_patterns.md` and specifications as needed

## Documentation & Patterns
- [x] Update `docs/specs.md` if architecture adjustments arise (with justification)
- [x] Capture architectural patterns in `docs/architectural_patterns.md` when stabilized

- [x] Review `docs/user_requests.md` at each working session and update plans/instructions as needed

## M10 - Cache Strategy Migration
- [x] Remove Redis dependency from codebase and packages
- [x] Introduce hybrid caching service using IMemoryCache and disk persistence
- [x] Update memory and tool components to use the new cache abstraction
- [x] Adjust configuration, documentation, and tests for disk-backed cache


