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
- [x] Centralize agent system prompts under shared configuration constants (2025-10-05)
- [x] Surface agent iteration/summarization limits via configurable options and appsettings template (2025-10-05)
- [ ] Conduct a self-driven research run of the app (once dependencies configured) to validate new capabilities and gather insights
- [x] Reflect resulting architectural adjustments in `docs/architectural_patterns.md` and specifications as needed


## M11 - Computer Use Search Integration
- [x] Extend configuration to allow choosing between Google API and Azure computer-use deployment
- [x] Implement computer-use driven search workflow with Playwright control and DOM extraction
- [x] Update WebSearchTool to route to the selected search provider and ensure caching/logging
- [x] Add startup readiness probe to verify Playwright availability and emit a single warning on failure
- [x] Provide configuration flag and runtime fallback to disable computer-use searches when browsers are missing
- [x] Add validation coverage for computer-use search mode (unit/integration as feasible) — see `ComputerUseSearchServiceIntegrationTests`

## M12 - Research Flow Visualization
- [x] Wire ResearchFlowTracker throughout orchestrator/researcher lifecycle
- [x] Ensure flow diagram files are emitted alongside report artifacts
- [x] Document flow tracking behavior and update specs/user requests

## M13 - Operational Diagnostics
- [x] Add CLI or scripted diagnostic path to enumerate Cosmos tool outputs for a session
- [x] Document workflow for inspecting stored scrape content when computer-use is disabled
- [x] Introduce `--diagnostics-mode` fast paths (starting with list-sources output) to avoid full regeneration cycles when only summaries are needed (completed 2025-10-05)
## M14 - Hybrid Search Exploration
- [x] Route hybrid Google API search results through an agent-driven selection step
- [x] Launch computer-use browsing sessions for each selected citation to capture richer context
- [x] Persist exploration outputs into memory and reference them in reporting pipelines
- [x] Expand automated tests to cover hybrid exploration workflow

## M14.1 - Structured Exploration Summaries
- [x] Propagate structured summary findings into CLI exploration summaries (2025-10-02)
- [x] Surface raw structured JSON output in diagnostics and CLI (2025-10-04)
## M15 - Navigator Flagging & Follow-up
- [x] Add deterministic repaint wait helper to computer-use interactions to stabilize screenshots
- [x] Capture structured flagged resources during exploration sessions
- [x] Route flagged pages to downstream scraping and queue downloads for file analysis
- [x] Document flagged resource workflow and update best-practices summary

## M16 - Computer-use Flagged Resource Processing Refinement
## M16 - Computer-use Flagged Resource Processing Refinement
- [x] Audit flagged resource classification gaps for PDFs and other binaries surfaced by computer-use.
- [x] Update follow-up orchestration to funnel flagged web pages into the scraper and flagged PDFs into the Document Intelligence pipeline automatically.
- [ ] Guard the scraper against binary responses and ensure file registry entries trigger downstream file reading.
- [ ] Extend automated coverage to validate the new branching logic for pages versus PDFs.
- [x] Harden computer-use browser flows to bypass DOM probing on PDF surfaces, wait for viewer readiness, and maintain session stability (2025-10-06)

## M17 - Report Generation Flow Compliance
- [x] Enforce hybrid search/exploration flow loops in `ResearcherAgent` across all providers.
- [x] Restructure `OrchestratorAgent` stages to follow analysis -> outline -> section writing -> general sections.
- [x] Capture iterative outline artifacts in memory and flow tracker for auditability.
- [x] Update synthesis/report builder logic to honour section ordering and outline decisions.
- [x] Allow outline sections to be marked structural-only so drafting skips headings that don't need narrative (2025-10-04)
- [x] Surface accepted planner plans throughout synthesis and reporting pipelines so the final report reflects the confirmed research strategy (2025-10-05)

## M18 - CAPTCHA Mitigation & Stealth Browser Posture
- [ ] Integrate Playwright stealth configuration (navigator.webdriver patches, realistic UA/timezone/viewport, persistent contexts, manual launch flags) in computer-use services per stealth blueprint.
	- [ ] Check in hardened Chromium launch profile hooked through `ComputerUseBrowserFactory` with locale/timezone alignment to proxy region.
- [ ] Evaluate stealth tooling options (soenneker.playwrights.extensions.stealth, Undetected.Playwright, rebrowser patches) and record decisions + trade-offs.
	- [ ] Run comparative diagnostics against `https://bot.sannysoft.com/` to quantify coverage per library and capture screenshots/logs.
	- [ ] Document reapplication steps if `rebrowser-patches` are adopted post-Playwright upgrade.
- [ ] Introduce human-like cursor/typing behavior simulation for CUA-issued actions with configurable parameters.
	- [ ] Design injection point in `ComputerUseSearchService` to route CUA commands through motion and typing simulators before execution.
- [ ] Implement rotating residential proxy and TLS/JA3 alignment strategy to reduce bot fingerprint discrepancies.
	- [ ] Extend `Settings` + configuration templates with residential proxy provider credentials and failover rules.
- [ ] Detect CAPTCHA interruptions and surface dedicated recovery events with retry guidance.
- [ ] Document mitigation strategy and add tests validating fallback behavior when CAPTCHA is encountered.

## Documentation & Patterns
## M19 - Report Regeneration & Quality Analysis
- [x] Attempt report-only regeneration for latest session and capture runtime diagnostics.
- [x] Review session logs to identify failure modes preventing stored findings.
- [x] Assess most recent generated report for structure, consistency, and citation quality.
- [x] Patch Cosmos persistence and embedding generation so findings persist reliably for regeneration.
- [x] Iterate on report layout/prompting to eliminate redundancy and tighten citation sourcing.
- [x] Update `docs/specs.md` if architecture adjustments arise (with justification)
- [x] Capture architectural patterns in `docs/architectural_patterns.md` when stabilized

- [x] Review `docs/user_requests.md` at each working session and update plans/instructions as needed

## M10 - Cache Strategy Migration
- [x] Remove Redis dependency from codebase and packages
- [x] Introduce hybrid caching service using IMemoryCache and disk persistence
- [x] Update memory and tool components to use the new cache abstraction
- [x] Adjust configuration, documentation, and tests for disk-backed cache
- [x] Configure cache directory path in appsettings.local.json for local development
- [x] Add report output directory setting and wire default usage when CLI args omitted







