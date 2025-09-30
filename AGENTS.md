# Protocol for the Autonomous Development Agent

**Objective:** This document provides the operational mandate for an autonomous AI development agent. Your sole directive is to autonomously develop, test, and build the "Marconian" deep research agent as a .NET console application, following the specifications in `docs/specs.md`. The entire development lifecycle, from project creation to a final compiled binary, must be performed without human intervention. Your final output is a verified, runnable application.

## 1. Core Mandate

Your fundamental goal is to act as the lead software developer for the Marconian project. Your task is to write, build, test, and verify the .NET console application described in the development plan. Your process is complete once a clean, tested, and runnable application has been successfully compiled.

You are to follow these instructions sequentially and precisely, while adhering to the Guiding Principles below. Failure to comply with a step requires you to halt, report the error, and await further instruction.

## 2. Guiding Principles

- **Adaptive Planning:** The initial specifications in `docs/specs.md` are your starting point, not an immutable script. As you proceed, you may identify more efficient algorithms, superior architectural patterns, or unforeseen challenges. You are empowered and expected to update `docs/specs.md` to reflect these new insights. Justify any significant changes in your development plan.
- **Dynamic Plan Management:** Before writing any code, you must create a detailed, step-by-step development plan in Markdown format at `docs/plans/main_development_plan.md`. This plan should break down each milestone from `docs/specs.md` into concrete, verifiable tasks (e.g., "Create file X," "Implement method Y," "Write test for class Z").
- **Real-Time Progress Tracking:** You must keep the development plan in `docs/plans/main_development_plan.md` constantly up-to-date. As you complete a task, mark it as done (e.g., using `[x]`). This serves as your working memory and provides a clear audit trail of your progress.
- **Self-Correction and Pivoting:** If you encounter a persistent error, a failing test that you cannot resolve, or a logical flaw in your plan, you must halt execution of the current task. You will then analyze the problem, update your development plan with a new strategy, and resume work.
- **Architectural Documentation:** As you implement the system, you may establish significant architectural patterns or reusable development practices. If a pattern becomes central to the agent's design, you are to document it in a dedicated file at `docs/architectural_patterns.md`. This ensures that your design decisions are recorded for future reference.

## 3. Autonomous Application Development Protocol

### Step 3.1: Environment & Project Scaffolding
1.  **Verify .NET SDK:** Execute `dotnet --version` to ensure the .NET 8 (or later) SDK is installed and accessible. If not, halt and report this prerequisite failure.
2.  **Initialize Project:** Create a new .NET console application within the `src/` directory. Name the project `Marconian.ResearchAgent`.
3.  **Install Dependencies:** Execute `dotnet add package` for each of the following required NuGet packages into the `Marconian.ResearchAgent` project:
    - `Azure.AI.OpenAI`
    - `Microsoft.Azure.Cosmos`
    - `Microsoft.Extensions.Caching.StackExchangeRedis`
    - `Azure.AI.DocumentIntelligence`
    - `Microsoft.Playwright`
    - `HtmlAgilityPack`
    - `Microsoft.Extensions.Configuration`
    - `Microsoft.Extensions.Configuration.Binder`
    - `Microsoft.Extensions.Configuration.EnvironmentVariables`

### Step 3.2: Configuration & Pre-flight Checks
1.  **Implement Configuration Service:** Create a static `Settings.cs` class that loads all required connection strings and API keys from environment variables as specified in `docs/specs.md`.
2.  **Validate Configuration:** Before proceeding, programmatically check that every required environment variable is present and not empty. The required variables are:
    - `AZURE_OPENAI_ENDPOINT`
    - `AZURE_OPENAI_API_KEY`
    - `COSMOS_CONN_STRING`
    - `AZURE_REDIS_CACHE_CONN_STRING`
    - `COGNITIVE_SERVICES_ENDPOINT`
    - `COGNITIVE_SERVICES_API_KEY`
    - `GOOGLE_API_KEY`
    - `GOOGLE_SEARCH_ENGINE_ID`
3.  **Halt on Failure:** If any variable is missing, you must halt the entire process and output a clear error message specifying which variables are not set. **Do not proceed with a partial configuration.**

### Step 3.3: Code Generation & Implementation
1.  **Create Development Plan:** Based on `docs/specs.md`, create the initial `docs/plans/main_development_plan.md` file. This plan must be your guide for all subsequent steps.
2.  **Core Services (M1):** Create wrapper services for the LLM (Azure OpenAI), Long-Term Memory (Cosmos DB), and Distributed Cache (Redis). These services must handle all client initialization, request execution, and error handling.
3.  **Agent & Tool Abstractions (M2):**
    - Define an `IAgent` interface with an `ExecuteTaskAsync` method.
    - Define an `ITool` interface with properties for `Name` and `Description`, and an `ExecuteAsync` method.
4.  **Memory Modules (M3):** Implement the short-term and long-term memory managers. The short-term memory must include the context-shrinking summarization logic. The long-term memory service must implement the vector embedding generation and in-memory cosine similarity search.
5.  **Tool Implementation (M4):** Create concrete classes for each tool (`WebSearchTool`, `WebScraperTool`, `FileReaderTool`, `ImageReaderTool`), ensuring they implement the `ITool` interface.
6.  **Agent Implementation (M5):**
    - Create the `OrchestratorAgent` class. It will manage the main state machine (`Planning` -> `Executing_Research_Branches` -> `Synthesizing_Results` -> `Generating_Report`).
    - Create the `ResearcherAgent` class. It will be responsible for executing a single research question using the available tools.
    - Implement the branching logic where the Orchestrator spawns `ResearcherAgent` instances using `Task.Run`.
7.  **Synthesis & Reporting (M6):** Implement the logic for the Orchestrator to collect results from all researcher tasks, synthesize them, and generate the final Markdown report with citations.
8.  **Main Entry Point (`Program.cs`):** Wire all components together. The main method should initialize the services, instantiate the `OrchestratorAgent`, provide it with an initial research query, and run its main execution loop.

### Step 3.4: Verification, Testing, & Final Build
1.  **Create Test Project:** Create a new NUnit test project named `Marconian.ResearchAgent.Tests`.
2.  **Install Test Dependencies:** Add `NUnit`, `NUnit3TestAdapter`, `Microsoft.NET.Test.Sdk`, and `Moq` NuGet packages to the test project.
3.  **Write Unit Tests:**
    - For core services (e.g., `MemoryService`), write unit tests using `Moq` to mock external dependencies like Cosmos DB or Redis clients.
    - Test logic for short-term memory summarization and long-term memory retrieval.
    - Test the `OrchestratorAgent`'s state machine logic in isolation.
4.  **Write Integration Tests:**
    - Write a limited set of integration tests for the most critical tools (e.g., `WebSearchTool`).
    - These tests will make live API calls and should validate the connection and basic response parsing. Use a non-empty check rather than asserting specific content.
5.  **Execute Tests:** Run `dotnet test` from the root directory. All tests must pass. If any test fails, you must enter a debugging loop to analyze the code, propose a fix, and re-run the tests until they all pass.
6.  **Final Build:** Once all tests pass, execute `dotnet build --configuration Release`. Confirm that the build succeeds without errors.
7.  **Report Completion:** Upon successful build, report that the development and verification process is complete. The application is ready for execution.
