## Context

Five production bugs causing agent hangs, orphan processes, stale DB rows, and inconsistent UI state. The root cause spans `AgentRunnerService` (shutdown/recovery), `WorkflowEngine` (no stage timeout), and `issues.ts` reopen API (no review-recovery shortcut). The codebase has no `workflow-controller.ts` — pipeline logic is split across `workflow-engine.ts`, stage runners (`check-stage-runner.ts`), and individual check classes.

Key architectural facts:
- `AgentRunnerService.shutdown()` currently only unsubscribes from eventBus — no agent abort, no map cleanup
- `coderSessionRepo` is passed to `AgentRunnerService` as `_coderSessionRepo?: unknown` (unused)
- `CoderSessionRepo` already has `findByIssueId()` and `updateStatus()` methods
- No `isReviewRecovery` concept exists yet — must be introduced
- `acp-session.ts` has per-round 15-min timeout but the Promise can hang indefinitely, blocking `check-stage-runner.ts`
- `coder_session.stage` column is never populated during insert in `acp-session.ts`

## Goals / Non-Goals

**Goals:**
- Fix all 5 bugs: shutdown cleanup, coder_session orphan cleanup, pipeline status guard, check stage timeout, reopen review-recovery shortcut
- Preserve existing pipeline flow — no stage restructuring
- Make the system self-healing on restart (recoverIssues handles all orphans)

**Non-Goals:**
- Adding a configurable pipeline timeout via `config.jsonc` (use constant for now)
- Refactoring stage runner architecture
- Adding a global agent activity monitor

## Decisions

### D1: Stage timeout wraps the entire check-stage-runner, not individual checks

Wrap `CheckStageRunner.run()` inside `executePipeline()` with a `Promise.race` + 30-min timeout. This is simpler and more reliable than adding timeouts to each check individually, because the hang originates in `AiReviewCheck` → `AcpRoundRunner` → `acp-session.ts` `conn.prompt()` which may never resolve/reject.

**Location:** `agent-runner-service.ts` `executePipeline()`, wrapping `pipeline.run(issue, acpOptions)`.

**Alternatives considered:**
- Timeout per ACP round in `acp-session.ts` — already exists (15 min) but doesn't fire when Promise hangs. Adding another layer there is unreliable.
- Timeout in `CheckStageRunner.run()` — too narrow, wouldn't protect other stages.
- Timeout in `WorkflowEngine.run()` — would require passing timeout config through the engine, more invasive.

### D2: inject `CoderSessionRepo` properly into `AgentRunnerService`

Change the constructor parameter from `_coderSessionRepo?: unknown` to `coderSessionRepo?: CoderSessionRepo`, store as class field. Use in `recoverIssues()` to clean up orphan sessions.

**Alternatives considered:**
- Cleanup via separate `CoderSessionService` — over-engineering for a simple `findByIssueId` + `updateStatus` loop.
- SQL `UPDATE coder_session SET status='failed' WHERE issue_id=? AND status='running'` bulk query — simpler but `CoderSessionRepo.updateStatus()` also sets `completed_at`, which is correct behavior.

### D3: Review recovery detection in reopen endpoint

Add `isReviewRecovery` logic to `POST /:number/reopen`: check if issue is `check` stage AND `hasCompletedCoderSession(issueId, 'check')` returns true. If so, set `approvalState` to `awaiting` directly instead of resuming pipeline.

**Caveat:** `hasCompletedCoderSession` queries by `stage` column, which is currently never populated in `acp-session.ts` inserts. Two options:
- (a) Fix `acp-session.ts` to pass `stage` during insert — correct but touches ACP layer
- (b) Use a different heuristic: check if `workflow_log` has `acp_session_completed` events for the issue — more reliable but more complex

**Decision:** Use option (a) — pass `stage` from `acpOptions.stage` (already available) into the `coderSessionRepo.insert()` call at `acp-session.ts:372` and `acp-session.ts:772`. This is a 2-line fix and makes `hasCompletedCoderSession` work correctly.

### D4: executePipeline status guard placement

Add `issueRepo.updateStatus(issue.id, IssueStatus.Active)` at the top of the async callback inside `executePipeline()`, before `pipeline.run()`. This is a defensive write that ensures consistency regardless of how `executePipeline` was reached.

### D5: Timeout clears the pending timeout timer

When the stage timeout fires, call `abortController.abort()` to signal the underlying SDK call, then let the `abortPromise` in `executePipeline` handle the rejection. The timeout timer must be cleared on normal completion to avoid leaks.

## Risks / Trade-offs

- **[CoderSessionRepo.updateStatus sets completed_at on 'failed']** → This is actually desirable — `completed_at` marks when the session ended, regardless of outcome.
- **[stage column not populated in existing coder_session rows]** → Old rows won't benefit from review recovery detection. Only new sessions after this fix will have correct stage values. Acceptable since old interrupted issues will be cleaned up by the `recoverIssues` coder_session cleanup.
- **[30-min timeout is a blunt instrument]** → Could kill a legitimately long-running review. Mitigation: 30 min is generous; the ACI internal 15-min per-round timeout should normally complete within that. The outer timeout is purely a safety net.
- **[abortController.abort() on shutdown may not kill the subprocess immediately]** → The opencode subprocess may linger briefly. This is acceptable — the important thing is that the agent loop stops and maps are cleared. The OS will clean up orphan processes.

## Migration Plan

No migration needed — all changes are backward-compatible:
1. `shutdown()` behavior change only affects running servers (no DB schema change)
2. `recoverIssues()` coder_session cleanup runs on next server restart, fixing existing orphans
3. `stage` column in `coder_session` starts populating for new sessions; old rows remain null
4. Reopen review-recovery activates automatically for sessions created after the fix
5. Rollback: revert the 5 file changes; no data migration to undo

## Open Questions

None — all implementation details are resolved.
