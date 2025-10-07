# Console Output Audit (2025-10-07)

## Snapshot (post-routing pass)
- `Console.WriteLine` usages in `src`: **154**
- `Console.Write` (non-newline) usages in `src`: **9**
- `Console.Error.WriteLine` usages in `src`: **1** (contained within `ConsoleStatusRouter` for stderr fallback)
- Direct console writes still appear in `Program.cs`, `Console/InteractiveResearchPlanner.cs`, and now the new helper `Streaming/ConsoleStatusRouter.cs`
- `ConsoleStreamWriter` remains the only implementation that writes to `Console.Out` intentionally as part of the streaming pipeline

## File-Level Distribution
| File | `Console.WriteLine` | `Console.Write` | `Console.Error.WriteLine` | Notes |
| --- | ---: | ---: | ---: | --- |
| `src/Marconian.ResearchAgent/Program.cs` | 111 | 5 | 0 | Menus + CLI prompts remain direct; session lifecycle, diagnostics, and error statuses now route through `ConsoleStatusRouter` |
| `src/Marconian.ResearchAgent/Console/InteractiveResearchPlanner.cs` | 43 | 4 | 0 | Interactive prompts still require direct writes; thought events mirror planner progress |
| `src/Marconian.ResearchAgent/Streaming/ConsoleStatusRouter.cs` | 0 | 0 | 1 | Central helper that writes to stderr for high-severity fallbacks when streaming is disabled |

## Message Category Breakdown (Program.cs)
1. **Configuration & Argument Errors** (fully routed)
   - Missing configuration and invalid CLI combinations now flow through `ConsoleStatusRouter` with `Error` severity while preserving stderr output when streaming is off.
2. **Computer-use Diagnostics** (partially routed)
   - Diagnostic kickoff, objective, concurrency notices, and completion/error messages now publish thought events alongside console output.
   - Multi-line summaries still render directly for CLI readability; consider structured publishing later.
3. **Session Lifecycle & Outcomes** (fully routed)
   - Session start/resume banners, completion status, summaries, report path, and error bullets now emit via the router.
4. **Session Management Menus & Usage Help** (intentionally direct)
   - Menus, prompts, and usage details remain console-only to honor interactive flows, but helper publishes a short "Displaying CLI usage" event.
5. **Diagnostics/Utility Outputs (outstanding)**
   - Session dump notifications and diagnostics reports are still printed directly; evaluate whether condensed summaries should be mirrored through the router.

## Message Category Breakdown (InteractiveResearchPlanner.cs)
- All writes support live user prompts, plan previews, and clarification flows.
- Existing `ThoughtPublisher` hooks already capture planner progress; no immediate change recommended beyond ensuring summaries stay in sync with new routing helper.

## Key Observations
- The vast majority of status-only messages (session start, completion, error rollups) bypass the streaming pipeline today, causing duplicated or missing summaries when streaming is enabled.
- Error reporting mixes `Console.Error` with logger calls; introducing a single routing abstraction will let us emit to stderr when streaming is disabled while raising `ThoughtSeverity.Error` events otherwise.
- Menu/usage text should stay on the console, but we can publish concise status summaries (e.g., "Interactive menu opened", "Deletion cancelled") to keep streaming viewers informed without echoing entire menus.

## Recommended Actions
1. ✅ Introduced `ConsoleStatusRouter` to centralize status emission, providing automatic streaming + console fallback logic.
2. ✅ Routed key configuration, session lifecycle, and computer-use diagnostic messages through the router; console output is preserved when streaming is disabled.
3. ⏱ Continue auditing long-form diagnostics/report outputs; consider adding summarized thought events without duplicating entire tables.
4. ⏱ Expand automated coverage around `ConsoleStatusRouter` and routed execution flows once message counts stabilize.

These follow-ups align with the remaining items under **M20 - Console Streaming UX Polish** in `docs/plans/main_development_plan.md`.
