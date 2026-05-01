## Context

The system already has four recovery endpoints — `start`, `reopen`, `retry`, `restart` — each covering a subset of failure scenarios. None of them combine orphan session cleanup with pipeline resume from the current stage. The `retry` endpoint is closest but requires `blocked` status and falls back to resetting to `backlog` when no checkpoint is found.

Key existing infrastructure:
- `AgentRunnerService.resumePipeline()` — launches pipeline from current issue stage, clears pending gates
- `CoderSessionRepo.updateStatus()` — updates individual session status (no bulk method yet)
- `CheckpointManager.delete(issueNumber, stage)` — clears stage-specific checkpoint
- `WorktreeManager.create()` / `getPath()` — worktree lifecycle
- `IssueRepo` — atomic updates for status, stage, approval_state, blocked_reason, retry_count

## Goals / Non-Goals

**Goals:**
- Single endpoint `POST /issues/:number/rerun` that works in any non-running state
- Clean up orphan coder sessions (`running` → `failed`) for the target issue
- Clear current stage checkpoint so the stage runs from scratch
- Resume pipeline from current stage, preserving prior stage outputs and worktree files
- Frontend "Rerun Stage" button on IssueDetailPage and IssueCard

**Non-Goals:**
- Auto-rerun on crash detection (server restart recovery already exists via `recoverIssues()`)
- Partial stage resume (use existing checkpoint system for that)
- Cleaning up coder sessions for other issues (scoped to target issue only)

## Decisions

### D1: Put rerun logic entirely in the API handler (not IssueService)

The existing `retry` endpoint is implemented inline in the API handler, not in `IssueService`. The rerun handler needs access to `agentRunner`, `coderSessionRepo`, `checkpointRepo`, `worktreeManager`, and `eventBus` — dependencies that `IssueService` doesn't currently hold. Adding them to `IssueService` would bloat it. Instead, keep rerun logic in the API handler, matching the `retry` pattern.

**Alternatives considered:** Add a `rerun()` method to `IssueService`. Rejected because it would require injecting 5 additional dependencies into the service for a single use case.

### D2: Add `failRunningByIssueId()` to CoderSessionRepo

`CoderSessionRepo` currently only has `updateStatus(id, status)` for single rows. Rerun needs to bulk-fail all `running` sessions for an issue. Add a new method `failRunningByIssueId(issueId)` that runs a single `UPDATE ... WHERE issue_id = ? AND status = 'running'` query instead of N individual updates.

**Alternatives considered:** Call `findByIssueId()` + loop `updateStatus()` in the handler. Rejected because it's N+1 queries and the bulk operation is a natural repo method.

### D3: Use `resumePipeline()` (not `startPipeline()`)

`resumePipeline()` is the correct call because: (1) it clears `pendingGates` for the issue, (2) it delegates to the same `executePipeline()` as start, and (3) the issue stage is already set — no transition needed. `startPipeline()` has extra guards (pending approval check, concurrent limit) that are redundant after our state cleanup.

### D4: Rerun rejects `draft` and `done` stages

- `draft` — no pipeline to rerun; use `start` instead
- `done` — pipeline completed; rerun would be confusing (use `reopen` + `start` if rework needed)

This keeps rerun scoped to mid-pipeline recovery.

### D5: Frontend reuses existing mutation pattern

Add a `rerunIssue()` method to `api.ts`, a `rerunMutation` via `useMutation` in both `IssueDetailPage` and `IssueCard`, and conditionally show the button when: stage is not `draft`/`done`, no agent is running on this issue. Same invalidation pattern (`['issues']`, `['agent-status']`) as other action mutations.

## Risks / Trade-offs

- [Race: agent crashes between `isRunning()` check and `resumePipeline()`] → Mitigated by `resumePipeline()` checking `activeAgents` internally and throwing if already running. The window is tiny and results in a 500 that the user can retry.
- [Orphan sessions from previous server run are invisible to `isRunning()`] → `isRunning()` checks in-memory `activeAgents` Map. After server restart, no agents are in the map, so `isRunning()` returns false — correct behavior. Orphan cleanup via `failRunningByIssueId()` handles the DB inconsistency.
- [Checkpoint repo not injected into API routes] → The `checkpointRepo` is already available in the API handler scope (used in the retry handler at line 2170).

## Migration Plan

No schema changes needed. All tables and columns already exist.

1. Add `CoderSessionRepo.failRunningByIssueId()` method
2. Add `POST /:number/rerun` route to `issues.ts`
3. Add `rerunIssue()` to frontend `api.ts`
4. Add rerun button UI to `IssueDetailPage.tsx` and `IssueCard.tsx`
5. Build, typecheck, test
