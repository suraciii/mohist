## Context

`recoverBuildStageIssue()` is called during server startup to handle orphaned issues in Build stage. It reads `tasks.json` to determine build progress. When all tasks pass, the method currently calls `updateStage(Review)` and returns — leaving the issue in `Active + Review` with no agent, no `pendingGate`, and no `approvalState`. This creates an unrecoverable deadlock.

The normal (non-crash) path works correctly: `executePipeline()` runs the `WorkflowController`, which handles build→review transition internally, sets `pendingGate` and `approvalState` when the review gate is hit. The recovery path bypasses all of this.

Key constraint: `resumePipeline()` requires `(issue, projectId, issueRepo, worktreePath, acpOptions, updateIssueStatus?)`. The recovery method already resolves `project`, `worktreePath`, and has `issueRepo` — but does **not** construct `acpOptions` (which needs `workflowLogRepo`, `coderSessionRepo`, `eventBus`, `opencodeBinPath`).

## Goals / Non-Goals

**Goals:**
- Fix the deadlock by resuming the review pipeline when all build tasks pass during recovery
- Gracefully degrade to `Blocked` if pipeline resume fails (e.g. concurrent agent limit)

**Non-Goals:**
- Refactoring the overall recovery architecture
- Adding new recovery paths for other stages
- Changing how `acpOptions` is constructed in the API layer

## Decisions

### D1: Use `startPipeline()` for the all-pass recovery branch

Instead of just `updateStage(Review)`, call `startPipeline()` with the review-stage issue. The `WorkflowController` will see `issue.stage === Review` and execute the review pipeline normally, eventually stopping at the approval gate and setting `pendingGate`.

This requires:
1. `updateStage(Review)` — move stage before start (already done)
2. Build `acpOptions` — the recovery method needs access to `workflowLogRepo`, `coderSessionRepo`, `opencodeBinPath` to construct `AcpConnectionOptions`
3. Check `startPipeline()` return value — if `{started: false}`, fall back to `Blocked`

**Why `startPipeline` over `resumePipeline`:** `resumePipeline()` throws on error and does NOT check `maxConcurrentAgents`. `startPipeline()` returns `{started, error}` gracefully and enforces the concurrent agent limit. The recovery path has no pending gate to clear (the whole point is that no gate was set), so `resumePipeline`'s `pendingGates.delete()` is irrelevant.

**Alternatives considered:**
- **`resumePipeline()`**: Throws on error, no concurrency check. Wrong fit for recovery where errors should be graceful.
- **Directly set `pendingGate` + `approvalState` without running the pipeline**: Would skip the review agent entirely. The review wouldn't actually run — no review report, no agent output. Wrong semantics.
- **Set stage back to Build and let user manually resume**: User has no way to resume from Build when tasks are all pass. Just shifts the problem.

### D2: Store already-passed constructor deps, add `opencodeBinPath`

The constructor already receives `_workflowLogRepo` and `_coderSessionRepo` (underscore prefix = discarded). Change them to stored class fields (`private readonly workflowLogRepo`, `private readonly coderSessionRepo`). Add `opencodeBinPath` as a new optional constructor parameter. All three are already available at the instantiation site in `server/index.ts:109`.

The recovery method builds `AcpConnectionOptions` from these stored fields plus `this.eventBus` (already a class field). This keeps the recovery self-contained — no API-layer callbacks needed.

**Alternatives considered:**
- **Pass `acpOptions` to `recoverIssues()`**: Would require changing the call site in server startup. Invasive and doesn't match the current pattern where recovery is self-contained.
- **Lazy-load from config**: `opencodeBinPath` and repos aren't derivable from config alone.

### D3: Issue object needs fresh stage before `startPipeline`

`startPipeline()` calls `executePipeline()` → `WorkflowController.run(issue)`. The controller reads `issue.stage` to determine what to execute. We must `updateStage(Review)` in the DB first, then re-fetch the issue (or mutate the in-memory object) so the controller sees `stage=Review`.

## Risks / Trade-offs

- **[Recovery runs during startup, may race with other recovered issues]** → `startPipeline` checks `maxConcurrentAgents` and returns `{started: false}` if limit hit. If that happens, fall back to `Blocked`. Safe.
- **[Constructor grows with more optional deps]** → Only 1 truly new param (`opencodeBinPath`). The other two (`workflowLogRepo`, `coderSessionRepo`) are already passed in, just discarded. Acceptable.

## Migration Plan

No migration needed. This is a bug fix that only affects the crash recovery path. Issues already stuck in the deadlock state will be handled by the new code on next server restart (they won't match `stage=Build` though — they're already `stage=Review`). For those, a manual DB fix is still needed, or a one-time migration that resets them to `Build` so recovery re-triggers.

## Open Questions

None.
