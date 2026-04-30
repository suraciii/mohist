# Review Report

## Result: PASS

All 26 new/modified tests pass. TypeScript compiles cleanly. Pre-existing test failures in `pipeline-controller.test.ts`, `pipeline-checkpoint.test.ts`, `agent-runner-service.test.ts`, `merge-queue.test.ts`, `priority.test.ts`, and `recover-issues.test.ts` are confirmed to exist on the base branch and are not introduced by this change.

## Dimensions

### Correctness: PASS

**Concurrent stop race condition** (warning): In `stop()`, the agent entry is deleted from `activeAgents` *before* `abortController.abort()` is called (line 285). This prevents double-stop from emitting duplicate `agent_stopped` events — the second call finds no entry and returns false. However, there's a brief window where `isRunning()` returns false but the abort hasn't fired yet. The `executePipeline` finally block also calls `this.activeAgents.delete(issue.id)`, which is a no-op if `stop()` already deleted it. This is safe.

**Stopped-while-running guard**: The `stoppedIssueIds` Set correctly prevents the catch block in `executePipeline` from setting error approval state and emitting `agent_error` for intentionally-stopped agents. The Set is populated before abort and cleaned up after promise settles.

**Approve handler approval state restore** (lines 899-933): When `force=true`, `stop()` clears approval state via `clearApprovalState()`. The approve handler saves `issue.approvalState` *before* calling `forceStopIfNeeded()`, then restores it after stop. This is correct.

**ACP session abort**: The `abortPromise` in `createAcpConnection` correctly races against `prompt()` and `timeout`. The `{ once: true }` option prevents listener leaks. When abort wins the race, `connection.cancel()` is called and `cleanup()` runs. The never-resolving fallback `new Promise<'aborted'>(() => {})` when no signal exists prevents false race wins.

**Minor**: The abort listener added in `abortPromise` is not explicitly removed when `prompt()` or `timeout` wins the race instead. Since it has `{ once: true }`, it will self-clean when the signal eventually fires, or be GC'd with the AbortController. No leak in practice.

### Complexity: PASS

- `stop()` method: 35 lines, cyclomatic complexity ~4 — clean.
- `forceStopIfNeeded()` helper: 20 lines, extracted to avoid duplicating the force-stop-then-check pattern across 4 handlers. Good deduplication.
- `executePipeline()`: ~140 lines total (including the IIFE), but the added abort logic is only ~10 lines. Acceptable.
- No copy-pasted logic across handlers — all use `forceStopIfNeeded()`.

### Test Coverage: PASS

- **T-005** (`agent-runner-stop.test.ts`): 17 tests covering:
  - stop non-existent agent (returns false)
  - stop running agent (cleanup + status)
  - pendingGates/waitingQuestions cleanup
  - agent_stopped event emission
  - double stop (second returns false)
  - approval state cleanup
  - no issueRepo scenario
  - race condition safety
  - AbortController + AbortSignal propagation through WorkflowController
  - concurrent stop calls
  - restart after stop

- **T-006** (`agent-stop-api.test.ts`): 9 tests covering:
  - POST /stop: 200 (running), 409 (no agent), 404 (missing issue)
  - POST /close?force=true: 200 (stops + closes)
  - POST /close (no force): 409
  - POST /reopen?force=true: 200
  - POST /reopen (no force): 409
  - POST /approve?force=true: 200
  - POST /reject?force=true: 200

- All 26 tests pass.

### Security: PASS

- No new SQL — uses existing `issueRepo.updateStatus()`, `issueRepo.clearApprovalState()` which are parameterized.
- No user input passed to shell or eval.
- `force` query param is a boolean string comparison (`=== 'true'`), no injection risk.
- Issue number parsed via `parseInt()` — safe for DB lookup.
- `stop()` operates only on `issueId` from internal state (activeAgents Map), not user-supplied strings.

### Spec Compliance: PASS

**T-001 — AgentRunnerService.stop()**:
- ✅ RunningAgent has abortController field (line 24)
- ✅ executePipeline passes AbortSignal to WorkflowController (line 429)
- ✅ stop(issueId) returns true when agent running (line 317)
- ✅ stop(issueId) returns false when no agent running (line 281)
- ✅ stop() removes activeAgents (line 285), pendingGates (line 298), waitingQuestions (line 299)
- ✅ stop() sets issue status to blocked (line 304)
- ✅ stop() emits agent_stopped event (line 314)
- ✅ stop() handles race condition safely (no throw, try/catch on await promise)
- ✅ Typecheck passes

**T-002 — AbortSignal propagation**:
- ✅ WorkflowControllerOptions has optional signal field (line 46)
- ✅ AcpConnectionOptions has optional signal field (acp-session.ts line 434)
- ✅ WorkflowController.run() checks signal.aborted between stages (lines 311-319, 343-351, 379-387)
- ✅ createAcpConnection prompt() races against signal abort (lines 748-783)
- ✅ When signal fires, connection.cancel() called and subprocess cleaned up
- ✅ Pipeline promise resolves via catch when signal aborts mid-stage
- ✅ Typecheck passes

**T-003 — POST /stop route**:
- ✅ Returns 200 with `{ success: true, data: { message } }` when agent stopped (lines 557-561)
- ✅ Returns 400 when no active project (lines 515-521)
- ✅ Returns 404 when issue not found (lines 524-530)
- ✅ Returns 409 when no agent running (lines 540-546)
- ✅ Returns 500 on internal error (lines 562-568)
- ✅ Typecheck passes

**T-004 — Force parameter**:
- ✅ POST /close?force=true stops agent then closes (200) (lines 593-598)
- ✅ POST /reopen?force=true stops agent then reopens (200) (lines 657-663)
- ✅ POST /approve?force=true stops agent then approves (200) (lines 903-912)
- ✅ POST /reject?force=true stops agent then rejects (200) (lines 1043-1049)
- ✅ Without force param, all four endpoints return 409 when agent running
- ✅ Force param is no-op when no agent is running
- ✅ Typecheck passes

**T-005 — Unit tests**:
- ✅ stop() on running agent returns true and cleans up state
- ✅ stop() on non-existent agent returns false
- ✅ stop() during agent self-completion does not throw
- ✅ AbortSignal aborts WorkflowController.run() between stages
- ✅ All 17 tests pass
- ✅ Typecheck passes

**T-006 — API integration tests**:
- ✅ POST /stop returns 200 when agent running
- ✅ POST /stop returns 409 when no agent running
- ✅ POST /stop returns 404 for non-existent issue
- ✅ POST /close?force=true returns 200 and closes
- ✅ POST /close returns 409 without force
- ✅ POST /reopen?force=true returns 200
- ✅ POST /approve?force=true returns 200
- ✅ POST /reject?force=true returns 200
- ✅ All 9 tests pass
- ✅ Typecheck passes

**Pipeline-model spec**:
- ✅ Pipeline can be interrupted at any stage (plan, build, review) via signal abort
- ✅ Issue status becomes `blocked` after interrupt
- ✅ Pipeline promise resolves (via catch) when signal aborts
- ✅ Issue can be reopened after stop (tested in T-005: "should allow restarting an issue after stop")

**http-api spec**:
- ✅ POST /pause returns 404 (no route registered)
- ✅ POST /resume returns 404 (route was removed)
- ✅ Approve endpoint remains active with force support

## Fix Suggestions

1. **`agent-runner-service.ts:285` (warning, not blocking)**: Consider moving `this.activeAgents.delete(issueId)` back after `await promise` to reduce the `isRunning()` false-negative window during stop. Currently safe because `stoppedIssueIds` Set prevents the catch block from acting, but the ordering could confuse future readers. The current implementation is functionally correct.

2. **`acp-session.ts:748-756` (minor)**: The `abortPromise` listener with `{ once: true }` is never explicitly removed if the prompt or timeout wins the race. While `{ once: true }` ensures eventual cleanup, wrapping in an `AbortController` or using `Promise.race` with cleanup would be more explicit. Not a functional issue.
