## Context

The merge subsystem has two independent code paths that both try to merge an issue's branch after agent completion:

1. **`agent_completed` handler** (server/index.ts:161-243): Directly calls `mergeBack()` → on conflict calls `mergeMasterInWorktree()` (reverse-merge master into worktree) → sets `mergeState=Resolving` → re-enters full pipeline via `agentRunner.startPipeline()` → `runConflictResolutionStage()` in WorkflowController.

2. **MergeQueue** (merge-queue.ts): Calls `rebaseOntoMaster()` → on conflict immediately aborts and sets `Blocked` with no agent resolution.

Problems: dual paths make behavior unpredictable; reverse-merge creates non-FF merge commits; conflict resolution re-enters pipeline (build→review→done cycle) unnecessarily; `runConflictResolutionStage()` is 160 lines of special-case code in WorkflowController.

Key files involved:
- `packages/cli/src/git/merge-queue.ts` (355 lines) — rewrite `processItem()`
- `packages/cli/src/git/worktree-manager.ts` (540 lines) — add `canFastForward()`, modify `rebaseOntoMaster()`, add `rebaseContinue()`, remove `mergeMasterInWorktree()`
- `packages/cli/src/server/index.ts` (276 lines) — simplify `agent_completed` handler, add `resolveConflicts` callback
- `packages/cli/src/workflow/workflow-controller.ts` — remove `runConflictResolutionStage()` and `MergeState.Resolving` dispatch
- `packages/cli/src/services/event-bus.ts` — add 3 event types
- `packages/cli/src/api/events.ts` — add events to `ALL_EVENT_TYPES`
- `packages/cli/web/src/components/MergeStatePanel.tsx` — add rebasing/resolving/blocked UI

## Goals / Non-Goals

**Goals:**
- Single merge entry point: all merges go through MergeQueue.enqueue()
- FF-only merges to master — clean git history
- Agent resolves rebase conflicts via direct ACP session (not pipeline re-entry)
- MergeQueue stays decoupled from agent runtime via `resolveConflicts` delegate
- 7 MergeState values with clear transitions

**Non-Goals:**
- Changing MergeState enum values (already has `Resolving`)
- Changing the agent runner's ACP session mechanism itself
- Auto-retry timers or scheduled retries (manual retry only)
- Handling concurrent agent sessions during conflict resolution (MergeQueue is serial)

## Decisions

### D1: resolveConflicts as delegate on MergeQueueDeps

MergeQueue calls `resolveConflicts(entry, worktreePath, conflictFiles)` when rebase conflicts persist after retry. The callback is injected by server and returns `{ success: boolean; error?: string }`. MergeQueue does not import or know about AgentRunnerService, ACP sessions, or prompts.

**Why:** Keeps MergeQueue testable without mocking agent infrastructure. Server owns the ACP session lifecycle.

**Alternatives considered:**
- Embedding agent logic directly in MergeQueue — couples git operations with agent runtime, hard to test.
- Using an event-based approach (emit conflict event, listen for resolution) — adds async complexity, harder to guarantee ordering in the serial merge queue.

### D2: rebaseOntoMaster({ abortOnConflict: false }) leaves markers

When `abortOnConflict` is `false`, the rebase leaves conflict markers in the worktree. MergeQueue can then hand the worktree to the agent for resolution, then call `rebaseContinue()`.

**Why:** The existing `rebaseOntoMaster()` always aborts on conflict, making it impossible to preserve conflict state for agent resolution. Adding an option avoids breaking existing callers while enabling the new flow.

**Alternatives considered:**
- Separate `rebaseWithConflicts()` method — duplicating rebase logic, more surface area.
- Having the agent initiate the rebase itself — agent would need git rebase knowledge, more error-prone.

### D3: One retry with fresh master before agent

On first rebase conflict: abort → `git fetch origin` → rebase again. Only if the retry also conflicts, invoke the agent. This handles the common case where master moved during the agent's work.

**Why:** Fetching fresh master is cheap and resolves many conflicts that arose from stale local state. Only escalate to agent when the conflict is genuine.

**Alternatives considered:**
- No retry, agent always resolves — wasteful for trivially stale conflicts.
- Multiple retries with backoff — adds complexity, delays resolution.

### D4: Agent conflict resolution via direct ACP, not pipeline

The `resolveConflicts` callback in server creates a direct ACP session with a conflict resolution prompt. The agent resolves markers, then the callback calls `rebaseContinue()`. No pipeline stages, no approval gates.

**Why:** The current `runConflictResolutionStage()` re-enters the pipeline, going through build→review→done with approval gates. This is 160 lines of special-case code that confuses the pipeline model. A direct ACP session is simpler and faster.

**Alternatives considered:**
- Keep pipeline re-entry but skip gates — still adds complexity to WorkflowController for a non-pipeline operation.
- Use opencode CLI directly — loses session tracking and event streaming.

### D5: recoverFromDB resets Resolving to Pending

When server restarts, issues in `Resolving` state get reset to `Pending` and re-enter the full merge flow (FF check → rebase → merge). The in-progress ACP session is lost anyway on restart.

**Why:** Simpler than trying to reconstruct ACP session state after restart. The rebase-first path is idempotent — re-running it is safe.

### D6: Remove runConflictResolutionStage from WorkflowController

Delete the entire method (~160 lines) and remove the `if (issue.mergeState === MergeState.Resolving)` dispatch in `runPipelineBuildStage()`. Also remove the `MergeState.Resolving` skip in the Review stage gate (line 371-375).

**Why:** Dead code after the refactor. Conflict resolution no longer goes through the pipeline. Removing it eliminates confusion about when/how conflict resolution happens.

## Risks / Trade-offs

- **[Agent fails to resolve conflict markers]** → Issue goes to `Blocked`, user can manually retry. Same terminal state as before but now with a single predictable path.
- **[rebaseOntoMaster with abortOnConflict=false leaves worktree in dirty state]** → If something crashes between rebase and agent resolution, worktree stays in rebase-in-progress. `recoverFromDB()` handles this by aborting and retrying from scratch.
- **[Build verification fails after agent conflict resolution]** → Merge is rolled back (`git reset --hard HEAD~1`), state goes to `BuildFailed`. Agent's conflict resolution work is preserved in the worktree — retry will rebase again cleanly.
- **[Server crash during agent ACP session]** → ACP session is lost. On restart, `recoverFromDB()` resets `Resolving` to `Pending` and retries. Agent may re-resolve the same conflicts.

## Migration Plan

1. **Phase 1 — WorktreeManager additions** (tasks.json T-001): Add `canFastForward()`, modify `rebaseOntoMaster()` signature with `abortOnConflict` option, ensure `rebaseContinue()`. Non-breaking — existing callers pass no options and get default `abortOnConflict: true`.

2. **Phase 2 — MergeQueue rewrite** (tasks.json T-002): Rewrite `processItem()` with FF check + rebase-first path + `resolveConflicts` delegate. Add `Resolving` to `recoverFromDB()` active states. MergeQueue is self-contained.

3. **Phase 3 — Events** (tasks.json T-003): Add 3 event types to EventMap and `ALL_EVENT_TYPES`.

4. **Phase 4 — Server handler simplification** (tasks.json T-004): Replace 80-line `agent_completed` handler with `mergeQueue.enqueue()`. Implement `resolveConflicts` callback with direct ACP session.

5. **Phase 5 — Dead code removal** (tasks.json T-005): Remove `mergeMasterInWorktree()`, `runConflictResolutionStage()`, `MergeState.Resolving` dispatch in WorkflowController, conflict-related skip in Review gate.

6. **Phase 6 — UI** (tasks.json T-006): Add rebasing/resolving/blocked states to MergeStatePanel.

7. **Phase 7 — Tests** (tasks.json T-007): Remove old tests, add new tests.

8. **Phase 8 — Verification** (tasks.json T-008): Build + typecheck + verify all tests pass.

**Rollback:** Since this is a refactor of internal flow (no DB schema changes, no API contract changes), rollback is reverting the git commits. MergeState enum is unchanged.

## Open Questions

None — all design decisions are resolved.
