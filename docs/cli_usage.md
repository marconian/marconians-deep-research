# Marconian Console & CLI Guide

This reference explains how to operate the Marconian deep research agent from the command line and through the interactive console workflow that now launches by default when no direct CLI operation is supplied.

## Entry Points

- **Interactive console** – Run the application with no arguments (`dotnet run --project src/Marconian.ResearchAgent`) to open the menu driven experience with planner chat, session management, and deletion helpers.
- **Direct CLI** – Provide one or more options to perform a specific operation without showing the menu. The CLI mirrors the console choices (new session, resume/report regeneration, listing, diagnostics, deletion, etc.).

> **Tip:** Use the `--help` switch at any time to print the latest usage block that matches the compiled application.

## Planner Integration

When you start a new session without the `--skip-planner` flag, the interactive research planner runs first. It asks the LLM to draft a plan, capture context hints, and confirm you are satisfied before handing off to the orchestrator. Both interactive and non-interactive flows capture the planner outcome so that context hints follow the session into execution. Set `--skip-planner` to jump directly to agent execution (a `PlannerSkipped: true` hint is stored for downstream pipelines).

## Session-Oriented Operations

| Option | Description |
| --- | --- |
| `--query <text>` | Create a brand new research session with the provided objective. The planner runs unless `--skip-planner` is also specified. |
| `--resume <session-id>` | Resume a previously stored session. The CLI validates that the session exists before running. |
| `--report-session <session-id>` | Regenerate a report for a completed session without launching new exploration. Equivalent to choosing "Regenerate completed report" from the console menu. |
| `--list-sessions` | Dump a table of recent sessions (ID, status, last update) and exit. Cannot be combined with other operations. |

## Reporting & Artifact Management

| Option | Description |
| --- | --- |
| `--reports <path>` | Override the output directory for synthesized reports and diagnostics artefacts. The path is created if it does not exist. |
| `--dump-session <session-id>` | Export stored memory records for a session to Markdown files. Combine with `--dump-type`, `--dump-dir`, or `--dump-limit` to filter output. |
| `--diagnostics-mode <mode>` | Run fast diagnostics without initiating the orchestrator. The `list-sources` mode prints the stored citations for a session specified via `--resume` or `--report-session`. |

## Web & Computer-Use Diagnostics

| Option | Description |
| --- | --- |
| `--diagnose-computer-use <url|optional objective>` | Launch a single guided computer-use exploration for troubleshooting. The URL is required; add `|` followed by context to steer the run. |
| `--cosmos-diagnostics` | Execute Cosmos DB vector index readiness probes and exit. Useful when preparing a new environment. |

## Session Deletion Controls

Deletion operations cannot be combined with other research actions. They align with the deletion submenu inside the interactive console.

| Option | Description |
| --- | --- |
| `--delete-session <session-id>` | Remove all Cosmos memory records and cached artefacts for a single session. |
| `--delete-incomplete` | Remove every stored session whose status is not `completed`. |
| `--delete-all` | Remove every stored session and associated report artefacts. |

During interactive usage you will be prompted to type `DELETE` to confirm. The CLI prints out the sessions that will be deleted and summarizes the number of Cosmos records removed.

## Planner & Menu Flow Overview

1. Application loads configuration, validates credentials, and initializes Cosmos, cache, tooling, and planner services.
2. Stored sessions are retrieved to populate menu summaries and deletion lists.
3. If no CLI task is in effect, the console menu offers:
   - Start new research session (with planner confirmation)
   - Resume existing session
   - Regenerate a completed report
   - Delete sessions (single, incomplete, or all)
   - Exit
4. Planner outcomes feed context hints into the orchestrator `AgentTask` to help downstream reasoning and reporting.
5. After execution results are persisted, the console prints success status, summary, and report path.

## Usage Patterns & Examples

- **Interactive new session:** `dotnet run --project src/Marconian.ResearchAgent`
- **Fire-and-forget session from automation:**
  ```pwsh
  dotnet run --project src/Marconian.ResearchAgent -- --query "Assess the state of quantum networking research" --reports c:\ResearchReports
  ```
- **Regenerate report without planner:**
  ```pwsh
  dotnet run --project src/Marconian.ResearchAgent -- --report-session 20241004abcdef --skip-planner
  ```
- **List sessions stored in Cosmos:**
  ```pwsh
  dotnet run --project src/Marconian.ResearchAgent -- --list-sessions
  ```
- **Delete incomplete sessions via CLI:**
  ```pwsh
  dotnet run --project src/Marconian.ResearchAgent -- --delete-incomplete
  ```

## Notes & Edge Cases

- Planner calls can fail if the LLM is unavailable. The app logs a warning and proceeds without planner context.
- Computer-use features require the Playwright browsers and relevant Azure deployment to be configured; otherwise they fall back to Google API mode or emit an error when forced.
- Diagnostic and dump commands require valid session IDs. The application prints clear errors if the session is missing.
- When deleting sessions, report artefact files referenced in metadata are removed from disk in addition to Cosmos documents.
