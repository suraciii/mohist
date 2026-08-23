# Issue 640: Cleanup Turn Admission Must Not Fail-Closed on Terminal-Fact Delivery Lag

## Why

When a Workflow agent turn completes with its artifacts recorded but the worktree still dirty, the runner immediately starts a bounded cleanup follow-up turn on the same Workflow AgentSession. The previous turn's terminal close/idle facts are already durable in the runner's runtime-event outbox, but their outbound delivery to the server lags — a normal property of the ordered outbox, not a fault. At the open instant the server still projects the session as `active`/`unknown`, so the OpenCode session-open guard rejects the cleanup turn fail-closed ("the previous Runtime Session has not reached a terminal state"), and the Pi cleanup channel rejects the frozen execution binding with a conflict because its original turn is not yet terminal server-side. A completed, artifact-intact task is thereby turned into a deterministic `worktree-dirty` failure (issues #602 and #600) before the cleanup prompt is ever delivered. Admission of a task's own cleanup turn is currently decided by the lagging session-activity projection instead of the authoritative frozen execution binding.

## What Changes

- A task's own cleanup follow-up turn (both OpenCode and Pi paths) waits — event-driven, within a bounded budget — for the immediately preceding turn's close/idle facts to complete outbound delivery to the server before opening the session and submitting the cleanup turn; admission then proceeds even though the pre-wait projection was `active`/`unknown`. The first cleanup attempt waits on the original Workflow turn, while later attempts correlate the preceding cleanup boundary and its Session-scoped `session-followup` facts by the deterministic cleanup operation id.
- The runtime-event outbox gains an event-driven delivery-completion wait for a cleanup predecessor's retained facts across the Workflow-scoped and Session-scoped producer families; no polling loops and no new server status round-trips are introduced.
- When the delivery wait exceeds its budget, the cleanup fails with structured evidence — the awaited session, the work item, and the budget — instead of a generic unsettled-session error.
- The existing fail-closed guard for a new task attempt reusing a genuinely pending session is preserved unchanged; only same-work-item cleanup admission stops consulting the lagging projection.
- Task success/failure is decided by the actual cleanup result. Cleanup prompt semantics, the cleanup attempt budget, and server-side frozen-binding validation are unchanged.

## Capabilities

- `cleanup-turn-admission`: Runner admission of a task's own worktree-cleanup follow-up turn converges on delivered terminal facts of the previous turn rather than the lagging session-activity projection, for both runtimes; budget-exhausted waits fail with session/work/budget evidence; cross-attempt reuse of genuinely pending sessions stays fail-closed.
- `runtime-event-delivery-wait`: The outbox exposes an event-driven, bounded wait for outbound delivery completion of the immediately preceding Workflow or cleanup turn's retained facts, including Session-scoped cleanup follow-up facts, without polling.

## Impact

- **Runner (TypeScript):** `packages/runner/src/runtime/executor-capabilities.ts` (OpenCode agent-turn session-open fail-closed guard vs. cleanup turns), `packages/runner/src/actions/pi.ts` (Pi turn and cleanup admission ordering), `packages/runner/src/actions/workflow-agent-session-reporter.ts` (terminal-fact delivery identity), `packages/runner/src/runtime/worktree-enforcement.ts` (cleanup-attempt failure evidence), and `packages/runner/src/server/runtime-event-outbox.ts` / `runtime-event-outbox-ports.ts` (new delivery-completion wait primitive).
- **Server:** No API or contract change. The `open` session status projection, the `cleanup-turn` route (`RunnerRoutes.WorkflowCleanup.cs`), and `AgentSessionGrain.WorkflowCleanup.cs` frozen-binding validation keep their current behavior and must not be weakened.
- **Tests:** Runner vitest coverage with fake timers for first and later cleanup-attempt admission under delivery lag (both runtimes), preserved fail-closed for cross-attempt reuse, budget-exhaustion evidence, and the non-polling wait semantics.
- **Dependencies:** None.
