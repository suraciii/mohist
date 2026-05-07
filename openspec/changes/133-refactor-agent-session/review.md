# Review Report

## Result: FAIL

---

## Dimensions

### Correctness — FAIL

Three critical bugs found:

**E1. `onBeforeKill` callback accepted but never invoked — WIP commit broken**

`AgentSessionOptions.onBeforeKill` is declared at `agent-session.ts:43` and passed by `ralph-executor.ts:714-719` to create WIP commits before killing timed-out processes, but `AgentSession` never calls it. The old `acp-session.ts` invoked `onBeforeKill` during timeout/exit cleanup. This is a silent data-loss regression: agents that time out will lose all in-progress work.

- **File**: `agent-session.ts:337-418` (`execute()` method)
- **Fix**: Before calling `this._acpProcess.cleanup()` on timeout/exit, invoke `this._options.onBeforeKill?.(this._options.cwd)` and propagate `wipCommitted` to the result.

**E2. `wipCommitted` never set — `timeout_with_wip` failure category is dead code**

`AcpSessionResult.wipCommitted` is declared at `agent-session.ts:52` but never set to `true` anywhere. The `categorizeFailure()` function in `ralph-executor.ts:58` checks `options?.wipCommitted` to distinguish `timeout_with_wip` from `timeout`, and the WIP resume context logic at `ralph-executor.ts:756-772` is unreachable.

- **File**: `agent-session.ts:398-402` (timeout result construction)
- **Fix**: Set `wipCommitted: true` in the result when `onBeforeKill` returns `true`.

**E3. Constructor parameter removal breaks 65 tests in 7 unmodified test files**

The `_agentSessionMessageRepo?: unknown` parameter was removed from `AgentRunnerService` constructor (`agent-runner-service.ts:103`), shifting all subsequent positional parameters by one. Seven test files that construct `AgentRunnerService` positionally were not updated:

- `tests/recover-issues.test.ts` — 24 failures
- `tests/api-routes.test.ts` — 8 failures
- `tests/recover-build-all-pass.test.ts` — 3 failures
- `tests/api-rebase.test.ts` — 4 failures
- `tests/e2e.test.ts` — 2 failures
- `tests/services/issue-task-queue.test.ts` — 2 failures

**Evidence**: `tests/recover-issues.test.ts:77-87` passes `(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, projectRepo, worktreeMock)` — the 8th arg `projectRepo` now maps to `worktreeManager` (was `checkpointRepo`), and `worktreeMock` maps to `taskQueueRepo` (was `worktreeManager`), causing `this.taskQueueRepo.findAllRunning is not a function`.

- **File**: `tests/recover-issues.test.ts:77-87` and 6 other test files
- **Fix**: Remove the `undefined` placeholder (position 5, the deleted param) from all test constructions, or convert the constructor to an options object.

**E4. Double SessionStateMachine creation**

In `AgentSession.create()`, a state machine is created in the constructor (`agent-session.ts:118`) then replaced with a new one after `coderSessionId` becomes available (`agent-session.ts:327-332`). The first machine's `transition('running')` at line 322 is not persisted to DB. The `initializing → running` transition is lost.

- **File**: `agent-session.ts:118,322,327-332`
- **Fix**: Initialize the state machine once after `coderSessionId` is available, or defer the first `transition('running')` until the DB-backed machine is created.

### Complexity — PASS with warnings

All functions are under 50 lines except `AgentSession.create()` (~130 lines, `agent-session.ts:207-335`) and `handleSessionUpdate()` (~50 lines, `agent-session.ts:142-191`). Cyclomatic complexity is under 10 for all functions. The longest file is `agent-session.ts` at 471 lines, exceeding the design target of 300 but representing a significant reduction from the original 954-line `acp-session.ts`.

### Test Coverage — FAIL

- Build passes (`tsc -b && vite build` succeeds)
- 10 test files were updated to use the new API and all pass
- **65 tests in 7 unmodified test files are broken** due to E3 (constructor parameter shift)
- All 1254 tests pass on master; all 65 failures are introduced by this branch
- No new test files were added for `AgentSession`, `SessionStateMachine`, `AcpProcess`, or `WorkflowSessionObserver`

### Security — PASS

- No injection risks: `AcpProcess` sanitizes env vars (removes `OPENCODE_SERVER_PASSWORD`, `OPENCODE_SERVER_USERNAME`)
- `process_pid` migration is additive and nullable — safe for production
- No secrets exposed in logs (IDs are truncated to 8 chars in log output)
- SQL queries use parameterized statements via `DatabaseManager`

### Spec Compliance — FAIL

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| AC1: `acp-session.ts` split into 3+ modules, each < 300 lines | **PARTIAL** | `acp-process.ts`: 118 ✓, `session-observer.ts`: 232 ✓, `session-state.ts`: 62 ✓, `agent-session.ts`: 471 ✗ (exceeds 300) |
| AC2: Eliminate 90% `runAcpSession`/`createAcpConnection` duplication | **PASS** | Both deleted; replaced by `AgentSession.create()` + `withSession()` |
| AC3: No `SessionManager` / `agent_session_message` code | **PASS** | `session.ts` deleted, `agent-session-message-repo.ts` deleted, migration v23 drops table, no references in source |
| AC4: ACP layer doesn't directly depend on EventBus/repos | **PARTIAL** | `agent-session.ts:30-34` still imports and passes `workflowLogRepo`, `sessionStreamLogRepo`, `eventBus`, `coderSessionRepo` to `buildWorkflowObserver()` (lines 68-85); actual repo calls are correctly in observer |
| AC5: New event sink requires no ACP layer changes | **PASS** | `SessionObserver` interface (`session-observer.ts:10-17`) supports arbitrary sinks; `observers` array in `AgentSessionOptions` |
| AC6: Server restart can recover session state from DB | **PASS** | `coder_session.process_pid` column added via migration, `CoderSessionRepo.findAllRunning()` at `coder-session-repo.ts:190-196`, orphan scan at `agent-runner-service.ts:280-293` queries it |
| AC7: All existing functionality works | **FAIL** | E1: `onBeforeKill` never invoked — WIP commit broken; E2: `wipCommitted` never set — `timeout_with_wip` category dead; E3: 65 tests failing |

---

## Positive Findings

1. **Clean dead-code removal**: `SessionManager`, `agent_session_message` table/repo/migration, and the `_agentSessionMessageRepo` constructor param all removed cleanly with zero dangling references in source code.
2. **Observer pattern well-designed**: `SessionObserver` interface with typed callbacks (`session-observer.ts:10-17`) is clean and extensible. `WorkflowSessionObserver` correctly consolidates all EventBus/repo side-effects.
3. **SessionStateMachine**: Centralized valid transitions (`session-state.ts:7-15`) with DB persistence — directly addresses the "state scattered across 5 modules" problem.
4. **`AcpProcess` extraction**: Process lifecycle (spawn/kill/SIGTERM→SIGKILL, stream setup, error handling) cleanly isolated at 118 lines (`acp-process.ts`).
5. **`process_pid` migration**: Nullable column with guard-check (`migrations.ts` — checks `hasProcessPid` before `ALTER TABLE`) is safe for production.
6. **SSE wire compatibility preserved**: `coder_text_chunk`, `coder_tool_call`, `coder_session_started` events unchanged — Web UI continues working.
7. **All 11 consumer files correctly updated**: `plan-stage-runner.ts`, `check-stage-runner.ts`, `ralph-executor.ts`, `skill-service.ts`, `explore-acp-service.ts`, `conflict-resolution.ts`, `build-test-check.ts`, `code-compiles-check.ts`, `stage-context.ts`, `workflow-engine.ts`, `server/index.ts` all properly use the new API.

---

## Warnings

**W1. `AgentSessionOptions` still contains business-logic dependencies**: Design D5 specified removing `workflowLogRepo`, `sessionStreamLogRepo`, `eventBus`, `coderSessionRepo` from options. They remain at `agent-session.ts:30-34` and are consumed by `buildWorkflowObserver()` at `agent-session.ts:68-85`.

**W2. Missing `AcpProtocol` module**: Design D2 specified three layers (`AcpProcess` / `AcpProtocol` / `AgentSession`). The `AcpProtocol` layer was not extracted — ACP protocol logic (ClientSideConnection, sessionUpdate handler) remains in `AgentSession` at `agent-session.ts:121-191`.

**W3. Silent observer error swallowing**: All 15 observer dispatch sites use empty `catch {}` blocks (`agent-session.ts:162,167,168,189,324,388,414,426,439`). The proposal explicitly identified this as an anti-pattern. Should log at minimum: `catch (e) { log.warn('observer error', ...) }`.

**W4. `onSessionEvent` swallows exceptions**: `session-observer.ts:181` has `catch {}` around stream_log/workflow_log writes — the exact anti-pattern from the proposal. Should log the error.

<promise>FAIL</promise>
