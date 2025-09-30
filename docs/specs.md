# Development Plan: Autonomous Deep Research Agent

This document outlines the development plan for creating 1.  **File Storage:** When the agent gathers files (images, PDFs, etc.), they will be saved to this directory with a manifest file (`manifest.json`) that tracks the original source URL, download timestamp, and a local file ID.
2.  **File Processing:** The `FileReaderTool` and `ImageReaderTool` will operate on files within this local registry. The extracted text and analysis will be stored in the long-term memory, linked back to the source file. The raw content of frequently accessed files can be cached in Redis to reduce disk I/O.

### 4.8. Report Generation
1.  **Final Synthesis:** This is the last stage of the Orchestrator's pipeline.
2.  **Structure Generation:** The agent will first generate an outline for the final report based on the synthesized findings.anced, autonomous deep research agent. The agent will be built as a .NET console application, designed for long-running, complex research tasks. It will leverage Azure services for its cognitive and memory functions and follow best practices for autonomous agent architecture.

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
    - **Model:** `gpt-5` (multimodal, as specified, pending availability)
    - **SDK:** `Azure.AI.OpenAI`
- **Long-Term Memory:** Azure Cosmos DB for NoSQL
    - **SDK:** `Microsoft.Azure.Cosmos`
    - **Usage:** Storing agent memories, retrieved documents, analysis, and relationship graphs between information entities.
- **Distributed Cache:** Azure Cache for Redis
    - **SDK:** `Microsoft.Extensions.Caching.StackExchangeRedis`
    - **Usage:** Caching short-term memory context, frequently accessed documents, and search results to improve performance.
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
2.  **Dependency Management:** Add NuGet packages for Azure OpenAI, Azure Cosmos DB, Azure AI Document Intelligence, Azure Redis Cache, Playwright, and configuration management.
3.  **Configuration Service:** Implement a service to securely load the following from environment variables:
    - `AZURE_OPENAI_ENDPOINT`
    - `AZURE_OPENAI_API_KEY`
    - `COSMOS_CONN_STRING`
    - `AZURE_REDIS_CACHE_CONN_STRING`
    - `COGNITIVE_SERVICES_ENDPOINT`
    - `COGNITIVE_SERVICES_API_KEY`
    - `GOOGLE_API_KEY` & `GOOGLE_SEARCH_ENGINE_ID`

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
    - **Redis Caching:** The `IDistributedCache` (backed by Redis) will be used to cache the agent's working context, allowing it to be shared and persisted across different processes if needed.
2.  **Long-Term Memory (Cosmos DB):**
    - **Schema Design:** Design a flexible Cosmos DB schema. A single container could store different document types (e.g., `type: "memory"`, `type: "document_chunk"`, `type: "source"`).
    - **Vector Storage:** While Cosmos DB for NoSQL doesn't have a native vector search (like the MongoDB vCore or PostgreSQL extensions do), we will store vector embeddings (generated via Azure OpenAI) alongside the text chunks.
    - **Vector Retrieval:** Implement a retrieval mechanism:
        1.  Generate an embedding for the current query/task.
        2.  Query Cosmos DB to retrieve all documents.
        3.  Perform a cosine similarity calculation in-memory against all stored vectors.
        4.  Return the top-k most relevant documents. This process can be accelerated by caching vectors in Redis.
    - **Memory Service:** Create a `MemoryService` that abstracts all interactions with Cosmos DB (saving memories, retrieving relevant information).

### 4.5. Perception & Tool Use Module
1.  **Tool Abstraction:** Define a common `ITool` interface with an `ExecuteAsync` method. Each tool will be a separate class implementing this interface.
2.  **Tool Development:**
    - `WebSearchTool`: Executes a query against the Google Custom Search API.
    - `WebScraperTool`: Takes a URL and extracts clean, readable text content.
    - `ImageReaderTool`: Takes an image URL or local path and passes it directly to the `gpt-5` model for analysis. The model's response (e.g., description, text extraction) is returned.
    - `FileReaderTool`: Reads content from local files. For PDFs, it will use the Azure AI Document Intelligence service to extract text and structure. For plain text files, it will use standard I/O.
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

### 4.8. Report Generation
1.  **Final Synthesis:** This is the last stage of the Orchestrator's pipeline.
2.  **Structure Generation:** The agent will first generate an outline for the final report based on the synthesized findings.
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
