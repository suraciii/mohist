## Context

The rebase capability exists in the codebase via `WorktreeManager.rebaseOntoMaster()` and `canFastForward()` (added by change #73), but is only called inside `MergeQueue.processItem()` — the automated post-completion merge flow. The user has no way to trigger rebase at earlier stages. The current architecture has:

- **Backend**: Hono-based API routes in `packages/cli/src/api/issues.ts`, services injected via factory function `createIssueRoutes()`. EventBus in `packages/cli/src/services/event-bus.ts` with typed `EventMap` and `ALL_EVENT_TYPES` array in `packages/cli/src/api/events.ts`. MergeQueue with `retry()` method in `packages/cli/src/git/merge-queue.ts`.
- **Frontend**: React + TanStack Query. IssueDetailPage in `packages/cli/web/src/components/IssueDetailPage.tsx` conditionally renders action buttons by `issue.stage`. MergeStatePanel has three "Retry Merge" buttons (for `build-failed`, `conflict`, `blocked` states). SSE via `useSSE` hook subscribes to a hardcoded `eventTypes` array.

## Goals / Non-Goals

**Goals:**
- Expose `POST /api/issues/:number/rebase` with stage-aware precondition checks
- Stage-specific post-rebase behavior: re-self-review (plan), checkpoint clear (build), build verify (review), MergeQueue retry (done)
- "Rebase onto master" button on IssueDetailPage for plan/build/review stages
- Rename "Retry Merge" → "Rebase and Retry" in MergeStatePanel
- SSE progress events for rebase operations

**Non-Goals:**
- Agent-based conflict resolution for plan/build/review rebase (abort on conflict, conservative strategy)
- Automatic rebase triggering (remains manual only)
- Build stage checkpoint partial clear (full clear only)
- New dedicated UI page or panel for rebase (reuse existing action button patterns)

## Decisions

### D1: Rebase orchestration lives in the API route handler, not a new Service

The rebase logic is a thin orchestration of existing primitives (`worktreeManager.canFastForward()`, `worktreeManager.rebaseOntoMaster()`, `mergeQueue.retry()`) with stage branching. Creating a dedicated `RebaseService` would add indirection without reuse benefit — the API route already has access to all required deps.

**Alternatives considered:** A `RebaseService` class with a single `rebase(projectId, issueNumber)` method. Rejected because the method would just be a pass-through to worktreeManager + stage switch, and no other consumer needs rebase orchestration beyond the API handler.

### D2: SSE progress events emitted inline during rebase, not via a background job

Rebase is fast enough (seconds) to execute synchronously in the request handler. We emit `rebase_started` before the operation, `rebase_progress` between steps (fetching → rebasing → verifying), and `rebase_completed`/`rebase_conflict` at the end. This matches the existing pattern where API handlers emit events directly via `eventBus.emit()`.

**Alternatives considered:** Background job queue. Rejected because rebase completes in seconds, doesn't need retry/queue semantics, and the SSE progress events give the UI real-time feedback.

### D3: Done stage rebase delegates to `mergeQueue.retry()` directly

When `stage === 'done'`, the rebase endpoint simply calls `mergeQueue.retry(issueNumber)` — identical to the existing `POST /:number/retry-merge` route. This avoids duplicating the MergeQueue's rebase-then-merge logic and keeps done-stage behavior unchanged under the hood. The only visible change is the UI button rename.

**Alternatives considered:** Reimplementing the MergeQueue flow in the rebase handler. Rejected because it would duplicate complex logic (build verify, rollback, worktree cleanup).

### D4: Plan stage re-self-review uses message injection, not a new pipeline stage

After a successful rebase in plan stage, we inject a message into the paused agent session (via `POST /api/issues/:number/messages` pattern — resuming the session with "master has new changes, check if design/tasks can leverage them"). This triggers a new LLM loop in the already-paused agent, which re-evaluates the design artifacts.

**Alternatives considered:** Creating a new "re-self-review" pipeline stage. Rejected because it would require changes to the pipeline state machine, and message injection achieves the same result by leveraging the existing approval gate mechanism.

### D5: Build stage checkpoint clear uses existing `checkpointRepo` methods

After rebase in build stage, we reset all tasks in `tasks.json` to `pending` status via the existing `PipelineCheckpointRepo`. The user then sees the tasks reset and clicks "Resume Pipeline" to restart from scratch.

**Alternatives considered:** Selective checkpoint preservation (keep tasks whose files weren't touched by rebase). Rejected due to complexity — determining which tasks are affected requires comparing task file paths against rebase diff, which is fragile.

### D6: Review stage build verify runs `npm run build` in the worktree directly

After rebase in review stage, we spawn a build command in the worktree directory (same pattern as MergeQueue's build verification). The result is returned in the API response as `buildPassed: boolean`. No automatic action is taken on failure — the user sees the result and decides.

**Alternatives considered:** Using the full pipeline build stage. Rejected because it would require stage transitions (review → build → review), adding complexity. A simple build command is sufficient for verification.

## Risks / Trade-offs

- **[Plan re-self-review prompt quality]** → The injected message must be specific enough for the agent to check for outdated file references. Mitigation: include explicit instruction "Check if any file paths referenced in tasks.json still exist after rebase."
- **[Build checkpoint clear is expensive]** → All tasks re-run from scratch after rebase. Mitigation: this is by design — task outputs may be invalid after rebase. The UI should clearly communicate this cost before the user clicks rebase.
- **[Review rebase conflict abort is conservative]** → User must manually resolve conflicts outside the system. Mitigation: this avoids the complexity of agent-based conflict resolution during an approval flow, and the UI shows the conflict file list so the user can address them.
- **[Race condition: user clicks rebase while agent starts]** → Precondition checks (agent not running) happen at request time, but agent could start between check and rebase execution. Mitigation: the rebase operation itself (`git rebase`) would fail if the worktree is locked by git, and we return the error to the user.

## Migration Plan

No migration needed — this is a purely additive change. The existing `/retry-merge` endpoint remains functional; the new `/rebase` endpoint is additive. The UI rename from "Retry Merge" to "Rebase and Retry" is a label change with no functional impact.

Deployment order:
1. Add event types to `EventMap` and `ALL_EVENT_TYPES` — backward compatible, frontend ignores unknown events
2. Add `POST /api/issues/:number/rebase` route — new endpoint, no existing behavior affected
3. Add `rebase` method to frontend API client
4. Add rebase SSE event handlers to `useSSE` hook
5. Add rebase button to IssueDetailPage — conditional on stage, no existing buttons removed
6. Rename "Retry Merge" → "Rebase and Retry" in MergeStatePanel — label change only

## Open Questions

- Should the build-stage rebase button show a confirmation dialog warning about checkpoint reset? (Leaning yes for P3 implementation)
- Should the plan-stage re-self-review message be configurable per project? (Leaning no for initial implementation)
