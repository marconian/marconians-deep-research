# Console Streaming UX Plan

## Objectives
- Remove chatty console logging while keeping persistent diagnostics flowing to debug and file targets.
- Stream meaningful LLM “thinking” output so users can follow research progress in real time.
- Handle parallel activity without chaos by batching raw events and summarizing batches into clean console updates.

## Current Pain Points
- Verbose `Console.WriteLine` usage in `Program.cs` and agents mixes structural updates with low-level logs.
- LLM responses are hidden until the call completes; users wait without feedback.
- Parallel research branches write independently, risking interleaved console output.

## Plan Overview

### Audit Current Console Output
- Identify every `Console.WriteLine` or direct console write in `Program.cs`, agents, and utility classes.
- Classify each message into:
    - **Structural updates** (startup, completion, error) – keep but route through a single console utility.
    - **Detailed logs/diagnostics** – redirect to structured logger sinks only.
    - **LLM responses or intermediate reasoning** – migrate to the streaming pipeline.

### Map All LLM Touch Points
- Catalog every call to `_openAiService.GenerateTextAsync`, `_openAiService.GenerateEmbeddingAsync`, and existing streaming helpers across:
    - Planning surfaces (`InteractiveResearchPlanner`, `OrchestratorAgent.GeneratePlanAsync`, replanning flows).
    - Branch execution (`ResearcherAgent` search triage, supervisor checks, analyst summaries).
    - Synthesis, outline creation, section drafting, and final report revision.
- For each call, capture prompt purpose, expected response scale (short JSON vs. long narrative), and current output handling to inform streaming hooks.

### Introduce Streaming-Friendly Abstractions
- Extend `IAzureOpenAiService` with a streaming method, for example `IAsyncEnumerable<OpenAiChatChunk>` or `Task StreamCompletionAsync(OpenAiChatRequest, Func<string, Task>)`.
- Provide helper wrappers so existing synchronous code paths either stream tokens to callbacks or aggregate responses into an in-memory buffer when streaming is unavailable.
- Ensure embedding generation and other non-streaming operations remain unaffected while sharing consistent telemetry hooks.

### Design the Console Streaming Pipeline
- Create a singleton `ConsoleStreamHub` with a thread-safe queue (e.g., `ConcurrentQueue<ThoughtEvent>`) and a background worker that coalesces raw events every _N_ seconds or after _M_ items.
- Implement helper APIs like `EnqueueThought(phase, actor, text, correlationId, severity)` plus structured metadata for log persistence.
- Allow the hub to invoke an optional summarizer that produces a single user-facing console update per batch while raw events are stored for diagnostics.
- Maintain ordering guarantees by timestamp while keeping the console output single-writer to prevent interleaving.

### Capture Thought Events Per Phase
- **Planning:** Emit events when the planner forms goals, enumerates sub-questions, or selects a final plan.
- **Search:** Stream search queries, tool rationale, and triage LLM decisions with chosen URLs.
- **Exploration:** Summarize scraper/file-reader outputs and flag noteworthy resources without dumping full content.
- **Synthesis & Outline:** Convert outline JSON into user-friendly headings and narrative direction updates.
- **Section Drafting & Revision:** Highlight key reasoning steps, citation tags, transitions, and subsequent markdown edits or “no change” outcomes.
- **Completion & Alerts:** Publish final report summaries and surface exceptions or cancellations immediately.

### Threading Strategy for Parallel Tasks
- Inside `ResearcherAgent.ExecuteTaskAsync`, tag each event with the branch ID (e.g., correlation GUID) to keep parallel workflows coherent.
- When spawning tasks via `Task.Run` in `OrchestratorAgent.ExecuteBranchesAsync`, route all messaging through the hub instead of writing directly to the console.
- Keep the hub as the sole console writer so parallelism cannot interleave user-facing output, while still permitting high concurrency for research operations.

### Update Logging Configuration
- Reconfigure `ILoggerFactory` to remove the console provider (or raise its minimum level to `Warning`) while retaining file and debug sinks.
- Replace ad-hoc `Console.WriteLine` calls with structured logging plus `ThoughtEvent` emission for anything user-relevant.
- Guarantee critical startup failures or fatal errors still reach the console through a dedicated alert pathway.

### Summarizer LLM and UX Considerations
- Implement a lightweight `ConsoleSummarizerAgent` that consumes batches of `ThoughtEvent`s and produces a concise paragraph or bullet list.
- Stream the summarizer’s output when possible; fall back to simple concatenation if the summarizer call fails or recursion limits are reached.
- Include progress affordances such as prefixes (`[Planning]`, `[Search: Branch-2]`) and timestamps for readability, and allow the summarizer to be disabled via CLI flag.

### API / UX Considerations
- Introduce CLI switches like `--stream-console` (default on) and `--no-stream-console` to opt out of streaming.
- Surface configuration under `ConsoleStreaming` (`Enabled`, `SummaryIntervalSeconds`, `BatchSize`, `UseSummarizer`, `SummarizerModel`).
- Document how users can adjust cadence or fallback behavior for environments without streaming support.

## Proposed Solution Overview
1. **Console Stream Hub** – central queue + background worker that emits curated updates.
2. **Thought Event Model** – standardized structure for streaming insights from each agent phase.
3. **Streaming LLM Wrapper** – extend Azure OpenAI service to support token streaming & callbacks.
4. **Summarizer Agent** – optional lightweight LLM (or heuristic fallback) to condense queued events.
5. **Logging Adjustments** – disable console logger; retain debug/file log sinks for diagnostics.

## Architecture
```mermaid
diagram TD
    subgraph Agents
        O[OrchestratorAgent]
        R[ResearcherAgent]
        P[Interactive Planner]
    end
    subgraph Services
        AI[AzureOpenAI Service]
        LTM[LongTermMemoryManager]
    end
    O -->|LLM requests| AI
    R -->|LLM requests| AI
    P -->|LLM requests| AI
    O -->|Thought events| Hub
    R -->|Thought events| Hub
    P -->|Thought events| Hub
    Hub[[ConsoleStreamHub]] --> Summarizer
    Summarizer -->|Console output| UserConsole
    Hub -->|Structured log| DebugLogs
```

## Thought Event Lifecycle
1. Agent prepares prompt metadata, enqueues `ThoughtEvent` (phase, actor, payload).
2. LLM response streamed via callback → incremental events pushed to queue.
3. Console hub batches events every 2–3 seconds or 5 items.
4. Summarizer agent produces concise message (e.g., single paragraph) with timestamp.
5. Output printed once; raw events persisted to debug log for traceability.

### Thought Event Schema
```csharp
record ThoughtEvent(
    string Phase,          // e.g., "Planning", "Search/Branch-2"
    string Actor,          // agent name
    string Detail,         // free-form text or JSON snippet
    DateTimeOffset Timestamp,
    Guid? CorrelationId = null,
    string? Severity = null // info|warn|error
);
```

## Phases & Required Surface Areas
| Phase                    | Sources                                                              | Console Signal                                                                 |
|-------------------------|----------------------------------------------------------------------|--------------------------------------------------------------------------------|
| Planning                | `InteractiveResearchPlanner`, `OrchestratorAgent.GeneratePlanAsync` | Numbered sub-questions, planner summary                                       |
| Search & Selection      | `ResearcherAgent` (search queries, triage decisions)                 | Query issued, rationale, chosen URLs                                          |
| Exploration (Tools)     | Web scraper/file reader outputs                                      | Short summary of extracted material, flagged resources                        |
| Synthesis & Outline     | Orchestrator synthesis + outline calls                               | Key narrative direction, outline headings                                    |
| Section Drafting        | Section author prompts & evidence summaries                          | Highlights of reasoning, citation tags, transitions                           |
| Report Revision         | Markdown editor pass                                                  | Applied edits or “no changes” decisions                                       |
| Errors / Alerts         | Exceptions, cancellations                                            | Immediate console notification via hub                                        |

## Streaming LLM Integration
- Add `Task StreamCompletionAsync(OpenAiChatRequest req, Func<string, Task> onChunk)` to `IAzureOpenAiService`.
- Provide fallback aggregator when streaming unsupported (existing `GenerateTextAsync`).
- Each agent method wraps LLM call:
  ```csharp
  await _streamingService.StreamCompletionAsync(request, async chunk =>
  {
      _consoleHub.Enqueue(new ThoughtEvent(phase, actor, chunk, DateTimeOffset.UtcNow));
      // optionally append to local buffer
  });
  ```

## Console Stream Hub Implementation Notes
- `ConcurrentQueue<ThoughtEvent>` with `SemaphoreSlim` to wake background worker.
- Worker merges events by phase & actor; ensures ordering by timestamp.
- Configurable cadence (`TimeSpan summaryInterval`, `int batchSize`).
- Exposes `IDisposable` or `StopAsync` to flush remaining items on shutdown.

## Summarizer Agent
- Lightweight prompt template: _“Summarize the following research updates in one concise paragraph.”_
- Accepts list of events → summarizer response streamed & printed.
- Failure fallback: join details with bullet list; mark summary as `[auto]`.
- Optionally throttle to avoid recursive LLM loops (max once per 10s).

## Logging Strategy
- Remove/disable `Console.WriteLine` except for critical startup failures.
- Logger pipeline: `Console` provider removed; keep `FileLogger`, `DebugLogger`.
- Console hub writes via `Console.Out.WriteLine` under single lock to avoid interleaving.
- Persist raw events to debug log: `_logger.LogInformation("[Thought] {@Event}", thoughtEvent)`.

## Parallelism Considerations
- Tag events with `CorrelationId` (branch ID or section ID) sourced from orchestrator branch metadata.
- Summarizer groups by phase & correlation to maintain narrative coherence and avoid mixing branches in one paragraph.
- Surface branch identity and status in console prefixes such as `[Search/B2] Selecting docs…` for quick scanning.

## CLI / Configuration
- New CLI flag: `--stream-console (default true)`; `--no-stream-console` disables hub.
- Config section `ConsoleStreaming` with:
    - `Enabled`, `SummaryIntervalSeconds`, `BatchSize`, `UseSummarizer`, `SummarizerModel`.
    - Optional overrides for summarizer rate limiting and fallback verbosity.

## Work Breakdown
1. **Scaffolding**
    - Audit current console usage and map replacement pathways.
    - Implement `ConsoleStreamHub`, `ThoughtEvent`, configuration section, and DI registration.
    - Disable default console logging while preserving startup error visibility.
        - [x] Console logging removed from `ILoggerFactory` setup; debug/file sinks retained (2025-10-07).
        - [x] `ConsoleStreamHub` instantiated in `Program.cs` with configuration-bound options and default summarizer/writer (2025-10-07).
2. **Streaming Service Enhancements**
    - Extend `IAzureOpenAiService` and concrete service with streaming APIs and helper wrappers.
    - Add fallbacks for models without streaming support.
3. **Agent Instrumentation**
    - Planning and replanning flows emit thought events and adopt streaming wrappers.
    - [x] Planner/orchestrator thought events wired for planning, continuation, synthesis, outline, and section drafting (2025-10-07).
    - Researcher branch flows (search, triage, exploration) stream updates tagged with branch IDs.
    - Synthesis, outline, section drafting, and revision phases emit user-friendly reasoning summaries.
4. **Summarizer & UX Layer**
    - Implement `ConsoleSummarizerAgent` with rate limiting, streaming support, and heuristics.
    - Wire CLI flags and configuration toggles; attach progress prefixes and timestamps.
5. **Testing & Validation**
    - Write unit tests for hub batching, summarizer fallback, and configuration defaults.
    - Create integration test with stubbed LLM to confirm ordering and summarizer output.
    - Run manual end-to-end session to capture console transcript and adjust cadence.

## Progress (2025-10-07)

- Disabled the legacy `AddSimpleConsole` logger in `Program.cs`, ensuring diagnostic output flows to debug/file sinks while freeing the console for streamed thought summaries.
- Bound `ConsoleStreaming` options via configuration and materialized the `ConsoleStreamHub` (default summarizer + writer) during startup, ready for downstream instrumentation.
- Added a reusable `IThoughtEventPublisher` with console-backed implementation and routed planner/orchestrator agents through it; first wave of planning and synthesis thought events now stream through the hub.

## Risks & Mitigations
- **Streaming unsupported in environment** → fallback to buffered responses, log warning.
- **Summarizer recursion/lag** → rate-limit summarizer, provide direct output fallback.
- **Console spam** → tune batch size/interval; allow user to disable at runtime.
- **Performance overhead** → ensure hub operations are async & lightweight; avoid heavy LLM summarizer for large batches by capping tokens.

## Testing Strategy
- **Unit:** Validate hub batching cadence, summarizer fallback formatting, configuration binding defaults, and event correlation tagging.
- **Integration:** Run a dry research session with stubbed LLM streaming to verify chronological summaries and alert handling.
- **Manual:** Execute `dotnet run --query "<sample>"` to visually confirm thought summaries without low-level logs; iterate on cadence settings.

## Next Steps
1. Review plan with stakeholders and confirm alignment on console UX goals.
2. Verify Azure OpenAI deployments support streaming for the selected models or document fallback path.
3. Implement scaffolding and configuration changes, then proceed through instrumentation and validation steps.

## LLM touchpoint inventory (2025-10-07)

### Text generation endpoints

| Location | Prompt persona/template | Output shape | Max tokens | Notes |
| --- | --- | --- | --- | --- |
| `Console/InteractiveResearchPlanner.InvokePlannerAsync` | `SystemPrompts.Planner.Coordinator` with `planner_response` JSON schema | Structured planner JSON (`PlannerResponse`) | Model default (not specified) | Drives initial/non-interactive plan proposals and clarification loops. |
| `Memory/ShortTermMemoryManager.SummarizeOldestEntriesAsync` | `SystemPrompts.Memory.ShortTermCompressor` | Narrative summary replacing oldest memory entries | 256 | Keeps short-term memory window bounded; output persisted back into memory ring. |
| `Agents/ResearcherAgent.ExecuteTaskAsync` | `SystemPrompts.Researcher.Analyst` | Markdown summary + citations for branch finding | 400 | Forms the branch-level finding stored to long-term memory and surfaced to orchestrator. |
| `Agents/ResearcherAgent.DecideNextSearchActionAsync` | `SystemPrompts.Researcher.Supervisor` | JSON payload (`IterationDecision` with action, queries, reason) | 300 | Governs search iteration loop, including continuation queries. |
| `Agents/ResearcherAgent.SelectCitationsForExplorationAsync` | `SystemPrompts.Researcher.SearchTriage` | JSON payload (selected citation indices + notes) | 350 | Picks which search results to pursue with scraping/automation. |
| `Tools/ImageReaderTool.DescribeImageAsync` | `SystemPrompts.Tools.ImageDescription` | Natural-language description of base64 image payload | 300 | Falls back to metadata when vision deployment unavailable. |
| `Agents/OrchestratorAgent.EvaluateResearchContinuationAsync` | `SystemPrompts.Orchestrator.ContinuationDirector` | JSON (`continue` flag + optional new branch questions) | 350 | Determines whether to spawn additional research passes. |
| `Agents/OrchestratorAgent.GeneratePlanAsync` | `SystemPrompts.Orchestrator.PlanningStrategist` | Enumerated branch questions (plain text parsed into plan) | 300 | Used when interactive planner context is absent or insufficient. |
| `Agents/OrchestratorAgent.SynthesizeAsync` | `SystemPrompts.Orchestrator.SynthesisAuthor` | Narrative synthesis covering collected findings | 600 | Seeds outline + section drafting with a holistic storyline. |
| `Agents/OrchestratorAgent.GenerateOutlineAsync` | `SystemPrompts.Orchestrator.OutlineEditor` with outline JSON schema | Structured outline (sections, layout, notes) | 500 | Primary source of section plans and layout hierarchy. |
| `Agents/OrchestratorAgent.DraftSectionAsync` | `SystemPrompts.Orchestrator.SectionAuthor` | Markdown section draft at research-grade depth | 600 | Consumes planner context, evidence, citations, and surrounding section cues. |
| `Agents/OrchestratorAgent.ReviseReportAsync` | `SystemPrompts.Orchestrator.ReportEditor` | JSON array of report edit instructions | 400 | Iterative markdown editing loop (up to `_maxRevisionPasses`). |

### Embedding endpoints

| Location | Input | Notes |
| --- | --- | --- |
| `Memory/LongTermMemoryManager.SearchRelevantAsync` → `GenerateEmbeddingAsync` | User query text | Embeds ad-hoc questions to retrieve similar findings from Cosmos DB via vector search. |
| `Memory/LongTermMemoryManager.StoreChunkedRecordsAsync` → `GenerateEmbeddingAsync` | Chunked memory content (split by token window) | Generates per-chunk embeddings before persisting records; validates dimension alignment and falls back to empty vector if model output mismatches Cosmos expectations. |
