# Development Plan: Autonomous Deep Research Agent

This document outlines the development plan for creating 1.  **File Storage:** When the agent gathers files (images, PDFs, etc.), they will be saved to this directory with a manifest file (`manifest.json`) that tracks the original source URL, download timestamp, and a local file ID.
2.  **File Processing:** The `FileReaderTool` and `ImageReaderTool` will operate on files within this local registry. The extracted text and analysis will be stored in the long-term memory, linked back to the source file. The raw content of frequently accessed files should be captured by the hybrid disk + memory cache to reduce disk I/O and share results across agent runs.

### 4.8. Report Generation
1.  **Final Synthesis:** This is the last stage of the Orchestrator's pipeline.
2.  **Structure Generation:** The agent will first generate an outline for the final report based on the synthesized findings.
### 4.9. Flow Tracking and Visualization
1.  **ResearchFlowTracker:** The orchestrator now records every major state transition (planning, branch execution, synthesis, report generation) into a flow tracker that emits Mermaid diagrams alongside the final Markdown report. Each branch, tool invocation, and revision pass is captured for post-run auditing.
2.  **Mermaid Output:** Flow diagrams are written to the same directory as the report. They provide an at-a-glance view of branch fan-out, tool usage, and report iteration history, making it easier to debug long research sessions.
3.  **Configuration:** Flow diagram output path mirrors the report directory and adopts CLI overrides. The tracker persists after each significant step so partially completed sessions still yield a diagram.
anced, autonomous deep research agent. The agent will be built as a .NET console application, designed for long-running, complex research tasks. It will leverage Azure services for its cognitive and memory functions and follow best practices for autonomous agent architecture.

## 1. Vision & Core Principles

The agent, codenamed "Marconian," will be an expert research assistant capable of taking a high-level research query and autonomously producing a comprehensive, detailed, and well-cited report.

- **Autonomy:** The agent will operate with minimal human intervention, managing its own tasks, memory, and learning processes.
- **Depth & Breadth:** It will perform deep dives into topics, branching out to explore related concepts in parallel before synthesizing the findings.
- **Evidence-Based:** All generated content will be grounded in verifiable information, with robust source citation.
- **Extensibility:** The architecture will be modular, allowing for the addition of new tools, skills, and capabilities over time.

## 2. High-Level Architecture

The agent's architecture will be a **hybrid multi-agent system**, inspired by the principles in the "LLM Autonomous Research Application Best Practices" document. A central **Orchestrator Agent** will manage the overall research process, spawning specialized **Researcher Agents** to handle parallel tasks.

- **Orchestrator Agent:**
    - The main "brain" of the operation.
    - Implements the "Deep Research" pipeline: Planning, Question Developing, and final Report Generation.
    - Manages the overall state of the research project.
    - Spawns, monitors, and synthesizes results from Researcher Agents.
- **Researcher Agents:**
    - Lightweight, specialized agents that run in parallel.
    - Each is assigned a specific sub-question or research angle by the Orchestrator.
    - Executes the "Web Exploration" and information gathering stage for its assigned task.
    - Can potentially spawn further sub-researchers if a topic is particularly complex (a recursive, branching model).
    - Reports its findings, including gathered data and sources, back to the Orchestrator.

This architecture allows for massive parallelism, resilience, and specialization, preventing the "tool-overload confusion" common in single-agent systems.

## 3. Technology Stack

- **Platform:** .NET 8+ Console Application
- **Language:** C#
- **LLM:** Azure OpenAI Service
    - **Model:** `gpt-5` (multimodal, as specified, pending availability); `text-embedding-3-large` (embedding generation)
    - **SDK:** `Azure.AI.OpenAI`
- **Long-Term Memory:** Azure Cosmos DB for NoSQL
    - **SDK:** `Microsoft.Azure.Cosmos`
    - **Usage:** Storing agent memories, retrieved documents, analysis, and relationship graphs between information entities.
- **Caching Layer:** Hybrid disk + in-memory cache
    - **Runtime:** `Microsoft.Extensions.Caching.Memory` for fast in-process reads
    - **Persistence:** Local disk snapshot store for reuse across sessions
    - **Configuration:** Override directories with optional `CACHE_DIRECTORY` and `REPORTS_DIRECTORY` settings
- **Web Interaction:**
    - **Search:** Google Custom Search API. While not entirely free, it provides a stable and powerful way to search. Free alternatives often rely on web scraping which can be unstable.
    - **Scraping:** Libraries like `HtmlAgilityPack` for HTML parsing and `Playwright` for dynamic sites.
- **File Processing:**
    - **Text:** Standard .NET I/O.
    - **PDF:** Azure AI Document Intelligence (via Cognitive Services SDK) for advanced text extraction.
    - **Images:** Natively handled by the `gpt-5` multimodal model.
- **Configuration:** `Microsoft.Extensions.Configuration` to load settings from environment variables.

## 4. Core Component Implementation Plan

### 4.1. Project Setup & Configuration
1.  **Create .NET Console Project:** Initialize a new .NET console application.
2.  **Dependency Management:** Add NuGet packages for Azure OpenAI, Azure Cosmos DB, Azure AI Document Intelligence, Playwright, hybrid caching (memory+disk), and configuration management.
3.  **Configuration Service:** Implement a service to load configuration from JSON (`appsettings.json`, `appsettings.local.json`) with environment variables overriding values. Required keys:
    - `AZURE_OPENAI_ENDPOINT`
    - `AZURE_OPENAI_API_KEY`
    - `AZURE_OPENAI_CHAT_DEPLOYMENT`
    - `AZURE_OPENAI_EMBEDDING_DEPLOYMENT`
    - `AZURE_OPENAI_VISION_DEPLOYMENT`
    - `COSMOS_CONN_STRING`
    - `COGNITIVE_SERVICES_ENDPOINT`
    - `COGNITIVE_SERVICES_API_KEY`
    - `GOOGLE_API_KEY`
    - `GOOGLE_SEARCH_ENGINE_ID`
    - `[Optional] COMPUTER_USE_ENABLED` (set to `false` to skip Playwright/computer-use search and force Google API mode)
    - `[Optional] PRIMARY_RESEARCH_OBJECTIVE` (default question when CLI args are omitted)
    - `[Optional] CACHE_DIRECTORY` (overrides the default `debug/cache` relative path)
    - `[Optional] REPORTS_DIRECTORY` (overrides the default `debug/reports` relative path)

    Explicit deployment names keep the Azure OpenAI wrapper configuration-driven instead of hardcoding resource IDs.

### 4.2. The LLM "Brain" & Profiling Module
1.  **LLM Service:** Create a wrapper service around the `Azure.AI.OpenAI` client. This service will handle request/response logic, token counting, and error handling.
2.  **Agent Profiling:** Define a `Profile` class that contains the system prompt, role, and expertise for an agent. The Orchestrator will have a "Lead Researcher" profile, while spawned agents will have "Specialist Researcher" profiles, dynamically customized with their specific task.

### 4.3. Reasoning & Planning Module (Orchestrator)
1.  **Task Decomposition:** The Orchestrator will take the user's main query and use the LLM to break it down into a structured research plan (Stage 1: Planning).
2.  **Question Generation:** The plan will then be refined into a list of specific, answerable questions (Stage 2: Question Developing). These questions become the tasks for the Researcher Agents.
3.  **State Machine/Graph Execution:** Implement a state machine or graph-based control flow (similar to LangGraph) to manage the lifecycle of the research: `Planning` -> `Executing_Research_Branches` -> `Synthesizing_Results` -> `Generating_Report`.

### 4.4. Memory Module
1.  **Short-Term Memory (Working Context):**
    - Each agent instance will have its own short-term memory manager.
    - This manager will hold the agent's profile, the current task, recent conversation history, and intermediate results.
    - To prevent context overflow, a summarization strategy will be implemented. Before a new turn, the oldest parts of the conversation history will be summarized by the LLM and replaced with the summary.
    - **Hybrid Cache:** Combine `IMemoryCache` for low-latency lookup with a disk-backed store so cached context survives process restarts without needing external infrastructure.
2.  **Long-Term Memory (Cosmos DB):**
    - **Schema Design:** Design a flexible Cosmos DB schema. A single container could store different document types (e.g., `type: "memory"`, `type: "document_chunk"`, `type: "source"`).
    - **Vector Storage:** Cosmos DB for NoSQL now provides native vector indexing. Store Azure OpenAI embeddings alongside the text chunks and configure DiskANN indexes with cosine distance for efficient recall.
    - **Vector Retrieval:** Implement a retrieval mechanism:
        1.  Generate an embedding for the current query/task.
        2.  Execute a Cosmos SQL query that orders results via `VectorDistance` inside the partition for the active research session.
        3.  Fall back to in-memory cosine similarity only if the vector query encounters an error.
        4.  Return the top-k most relevant documents, annotating each with the computed similarity score.
    - **Memory Service:** Create a `MemoryService` that abstracts all interactions with Cosmos DB (saving memories, retrieving relevant information) and persists orchestrator session state (session metadata, branch progress, tool telemetry) for resumability.

### 4.5. Perception & Tool Use Module
1.  **Tool Abstraction:** Define a common `ITool` interface with an `ExecuteAsync` method. Each tool will be a separate class implementing this interface.
2.  **Tool Development:**
    - `WebSearchTool`: Executes a query against the Google Custom Search API.
    - `WebScraperTool`: Takes a URL and extracts clean, readable text content.
    - `ImageReaderTool`: Takes an image URL or local path and passes it directly to the `gpt-5` model for analysis. The model's response (e.g., description, text extraction) is returned.
    - `FileReaderTool`: Reads content from local files. For PDFs, it will use the Azure AI Document Intelligence service to extract text and structure. For plain text files, it will use standard I/O.
    - `ComputerUseNavigatorTool`: Drives the Azure computer-use browser, waits for visual stability before capturing frames, and returns JSON that includes both a summarizing narrative and flagged resources (pages, downloads) for follow-up processing.
3.  **Tool Selection:** The agent's reasoning loop will involve presenting the available tools to the LLM and asking it to choose the best tool and provide the necessary arguments for the current sub-task.

### 4.6. Parallel Research & Branching
1.  **Task Management:** The Orchestrator will manage a queue of research questions.
2.  **Dynamic Instantiation:** For each question, the Orchestrator will instantiate a new `ResearcherAgent` task (`Task.Run`).
3.  **Isolation:** Each `ResearcherAgent` will have its own scope of work, its own short-term memory, and will report its findings back to a central data store (e.g., a specific partition in Cosmos DB for that research job).
4.  **Collapse/Synthesis:** Once all `ResearcherAgent` tasks are complete, the Orchestrator enters the synthesis stage. It will:
    - Read all the findings from the various branches.
    - Use the LLM to identify themes, reconcile conflicting information, and build a coherent narrative.

### 4.7. Information Gathering & Local Registry
1.  **Workspace:** For each main research query, create a unique directory on the local file system.
2.  **File Storage:** When the agent gathers files (images, PDFs, etc.), they will be saved to this directory with a manifest file (`manifest.json`) that tracks the original source URL, download timestamp, and a local file ID.
3.  **File Processing:** The `FileReaderTool` and `ImageReaderTool` will operate on files within this local registry. The extracted text and analysis will be stored in the long-term memory, linked back to the source file.
4.  **Flagged Resource Pipeline:** Whenever the computer-use navigator flags additional pages or downloadable assets, the orchestrator will queue them for the `WebScraperTool` and `FileReaderTool` automatically so that mid-session discoveries are captured without extra user input.

### 4.8. Report Generation
1.  **Final Synthesis:** This is the last stage of the Orchestrator's pipeline.
2.  **Structure Generation:** The agent will first generate an outline for the final report based on the synthesized findings.
### 4.9. Flow Tracking and Visualization
1.  **ResearchFlowTracker:** The orchestrator now records every major state transition (planning, branch execution, synthesis, report generation) into a flow tracker that emits Mermaid diagrams alongside the final Markdown report. Each branch, tool invocation, and revision pass is captured for post-run auditing.
2.  **Mermaid Output:** Flow diagrams are written to the same directory as the report. They provide an at-a-glance view of branch fan-out, tool usage, and report iteration history, making it easier to debug long research sessions.
3.  **Configuration:** Flow diagram output path mirrors the report directory and adopts CLI overrides. The tracker persists after each significant step so partially completed sessions still yield a diagram.

### 4.10. Operational Diagnostics
1.  **Session dump CLI:** The console application exposes `--dump-session <sessionId>` to export long-term memory artifacts (defaulting to `tool_output::WebScraper` records) into Markdown files for offline review.
2.  **Filtering & Limits:** Optional flags `--dump-type`, `--dump-dir`, and `--dump-limit` customize which records are exported, where they land on disk, and how many entries are collected.
3.  **Use Cases:** This path lets operators review previously scraped HTML payloads even when Playwright/computer-use tooling is disabled on the current machine.

3.  **Content Writing:** It will then iterate through the outline, writing detailed content for each section, pulling in the evidence and synthesized knowledge from its memory.
4.  **Citation Management:** As it writes, the agent will be prompted to include citations. The `Source` documents stored in Cosmos DB will be used to generate formatted citations (e.g., `[Source 1: URL]`).
5.  **Output Format:** The final report will be saved as a Markdown file, allowing for rich formatting.

## 5. Milestones

- **M1: Core Infrastructure.** Setup project, configuration, and basic LLM/Cosmos DB service wrappers.
- **M2: Single-Agent Prototype.** Build a single agent that can perform a simple search-and-summarize task. Implement basic short-term memory and one tool.
- **M3: Long-Term Memory.** Implement the Cosmos DB memory service with vector storage and retrieval.
- **M4: Multi-Tool Integration.** Develop the full suite of tools (web search, scraping, file/image reading).
- **M5: Orchestrator & Branching.** Implement the Orchestrator and the logic for spawning and managing parallel `ResearcherAgent` tasks.
- **M6: Synthesis & Reporting.** Develop the final report generation and citation capabilities.
- **M7: Testing & Refinement.** End-to-end testing, performance tuning, and prompt engineering refinement.











