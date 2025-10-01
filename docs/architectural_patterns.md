# Architectural Patterns

## Multi-Agent Orchestration
- The `OrchestratorAgent` implements a deterministic state machine (Planning ? Executing_Research_Branches ? Synthesizing_Results ? Generating_Report) to coordinate all work.
- Research work fans-out via `Task.Run` spawned `ResearcherAgent` instances, each operating on an isolated `ResearchBranchContext` snapshot.
- Each agent exchanges information exclusively through the memory services and shared tool abstractions, avoiding tight coupling and enabling horizontal scaling.

## Tool Plug-in Model
- All tools implement the `ITool` contract (`Name`, `Description`, `ExecuteAsync`) so the orchestrator can enumerate capabilities dynamically.
- Tools receive strongly-typed requests and return `ToolExecutionResult` objects that encapsulate success state, structured payloads, telemetry, and provenance metadata for citation.
- Retry, logging, and exception guards are handled inside each tool to keep agent loops focused on reasoning rather than infrastructure concerns.

## Memory Layered Architecture
- Short-term memory layers an in-process `IMemoryCache` with a disk snapshot store plus LLM-driven summarization to keep the working context beneath the token ceiling while retaining salient facts. Each researcher instance streams telemetry through structured logging so cache contents can be traced during investigations.
- Long-term memory persists to Cosmos DB configured in gateway mode with DiskANN vector indexing. Embeddings generated through the Azure OpenAI wrapper are written alongside session and branch state snapshots, enabling fast semantic recall and resumable workflows even after shutdowns.
- Both layers share the `MemoryRecord` model, enabling seamless promotion/demotion of knowledge between horizons and allowing orchestrator session metadata to live in the same container as tool evidence.

## Service Wrappers & Configuration
- External dependencies (Azure OpenAI, Cosmos DB, hybrid cache, Document Intelligence) are abstracted behind thin services that encapsulate client initialization, resilience policies, and usage telemetry.
- The static `Settings` class centralizes environment variable validation, preventing the application from starting if mandatory secrets are absent.

## Reporting & Provenance
- Synthesis workflows operate over normalized `ResearchFinding` models, merging redundant insights and preserving source citations.
- The final Markdown reporter template ensures every claim references a `MemoryRecord` identifier, maintaining an auditable trail from output back to raw evidence.
- After the first draft is emitted, an iterative revision loop asks the LLM for JSON-formatted line edits. A lightweight `MarkdownReportEditor` applies `replace`/`insert` operations so the agent can rework specific lines without regenerating the entire document.


