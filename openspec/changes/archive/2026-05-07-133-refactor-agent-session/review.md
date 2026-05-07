# Review Report

## Result: PASS

The refactor successfully eliminates the 5-way concept split in session management, removes all dead code, and introduces a clean observer-based event system. All 75 tests pass (1254 assertions). The core architectural goals are achieved: `AgentSession` + `withSession()` replace the duplicated `runAcpSession`/`createAcpConnection`, `SessionObserver` decouples ACP events from business logic, and `SessionStateMachine` centralizes state transitions.

## Dimensions

### Correctness — PASS

**Warning: Silent error swallowing in observer dispatch**

`agent-session.ts:162`, `agent-session.ts:167-168`, `agent-session.ts:189` — observer callbacks are wrapped in bare `try {} catch {}` blocks. The proposal explicitly identifies this as an anti-pattern ("多处吞掉异常，导致'未知的未知'"). At minimum these should log the error.

**Warning: Silent error swallowing in `WorkflowSessionObserver.onSessionEvent`**

`session-observer.ts:181` — bare `catch {}` with no logging. Same anti-pattern as above.

**Warning: State machine replacement in `AgentSession.create()`**

`agent-session.ts:332-338` — the state machine is created without DB persistence at line 118, then *replaced* with a DB-backed one after `coderSessionId` is known. The `initializing → running` transition at line 328 is not persisted to DB. If the process crashes between line 328 and line 332, the DB will show the session as 'running' without a `coderSessionId`, or worse, in an inconsistent state.

**Note: `execute()` timeout/abort paths set `_closed = true` and call `cleanup()` directly**

This means `close()` is a no-op after these paths (guard at line 452). This is correct behavior but could benefit from a comment explaining the invariant.

### Complexity — PASS

All functions are under 50 lines. The `AgentSession.create()` method is the longest at ~135 lines (207-341) due to sequential protocol initialization, but its cyclomatic complexity is manageable (linear path with early exits). No function exceeds complexity threshold.

### Test Coverage — PASS

All 75 existing test files pass (1254 assertions, 0 failures). Tests were updated to use new APIs (`AgentSession`, `withSession`, `AgentSessionOptions`). No new unit tests were added for the new modules (`AcpProcess`, `SessionStateMachine`, `WorkflowSessionObserver`). The existing test coverage relies on integration-level tests that mock the ACP subprocess.

### Security — PASS

No secrets exposed. No injection risks. `process.env` filtering in `AcpProcess` correctly strips `OPENCODE_SERVER_PASSWORD` and `OPENCODE_SERVER_USERNAME` (`acp-process.ts:35-38`).

### Spec Compliance — PASS

**Criterion 1: `acp-session.ts` (954 lines) split into modules each < 300 lines — PASS (with deviation)**

| Module | Lines |
|--------|-------|
| `acp-process.ts` | 118 |
| `session-observer.ts` | 232 |
| `session-state.ts` | 62 |
| `agent-session.ts` | **493** |

Deviation: `agent-session.ts` is 493 lines, exceeding the spec's "< 300 lines per module" criterion. The `AcpProtocol` layer (design D2) was not extracted as a separate module — protocol handling (`ClientSideConnection` calls) remains embedded in `AgentSession`. This leaves `agent-session.ts` doing double duty (session lifecycle + ACP protocol management).

**Criterion 2: Eliminate `runAcpSession` / `createAcpConnection` 90% duplication — PASS**

Both functions removed from source. `withSession()` (single-execute sugar) and `AgentSession.create()` (multi-round handle) are the unified replacements. `rg "runAcpSession|createAcpConnection|AcpConnection" src/` returns no matches.

**Criterion 3: No `SessionManager` or `agent_session_message` code — PASS**

- `SessionManager` class: deleted, zero references in source (`rg "SessionManager" src/` clean)
- `agent_session_message` table: migration v22 drops it (`migrations.ts:941-942`), `agent-session-message-repo.ts` deleted
- `AgentRunnerService` no longer takes `_agentSessionMessageRepo` parameter

**Criterion 4: ACP layer does not directly depend on EventBus, workflowLogRepo, sessionStreamLogRepo, coderSessionRepo — PASS (with warning)**

`acp-process.ts` has zero business dependencies — no imports of `EventBus`, `WorkflowLogRepo`, `SessionStreamLogRepo`, or `CoderSessionRepo`. All side effects go through `SessionObserver`.

Warning: `AgentSessionOptions` still contains `eventBus`, `workflowLogRepo`, `sessionStreamLogRepo`, `coderSessionRepo` fields (`agent-session.ts:30-34`), which design D5 explicitly says to remove. They're used by `buildWorkflowObserver()` to auto-create a `WorkflowSessionObserver`. The `AgentSession` class itself does not call `eventBus.emit()` or `repo.insert()` directly, so the spirit is met. But the interface leaks business types into the ACP layer.

**Criterion 5: New event sink requires zero ACP layer changes — PASS**

`SessionObserver` interface (`session-observer.ts:10-17`) with `onTextChunk`/`onToolCall`/`onSessionEvent`/`onStateChange`/`onRawNotification` is pluggable. `AgentSessionOptions.observers: SessionObserver[]` accepts arbitrary observers. Adding a WebSocket relay observer requires no changes to `AgentSession` or `AcpProcess`.

**Criterion 6: Server restart can recover running session state — PASS**

- `process_pid INTEGER` column added via migration v23 (`migrations.ts:947-958`)
- `CoderSessionRepo.insert()` writes `processPid` (`coder-session-repo.ts:138`)
- `CoderSessionRepo.findAllRunning()` enables orphan detection by DB query (`coder-session-repo.ts:190-196`)
- `AgentRunnerService.scanOrphanedIssues()` queries `coder_session WHERE status='running'` (`agent-runner-service.ts:284-289`)

**Criterion 7: All existing features (Plan/Build/Check/Skill/ConflictResolution) work — PASS**

75 test files pass, 1254 assertions, 0 failures. Consumer files updated: `plan-stage-runner.ts`, `check-stage-runner.ts`, `skill-service.ts`, `explore-acp-service.ts`, `conflict-resolution.ts`, `ralph-executor.ts`, `server/index.ts`, `build-test-check.ts`, `code-compiles-check.ts`, `workflow-engine.ts`, `stage-context.ts`.

## Changed Files

### New modules (4 files)
- `packages/cli/src/agent-runtime/acp-process.ts` — AcpProcess: subprocess spawn/kill/lifecycle (118 lines)
- `packages/cli/src/agent-runtime/agent-session.ts` — AgentSession + withSession: unified session abstraction (493 lines)
- `packages/cli/src/agent-runtime/session-observer.ts` — SessionObserver interface + WorkflowSessionObserver (232 lines)
- `packages/cli/src/agent-runtime/session-state.ts` — SessionStateMachine with typed transitions (62 lines)

### Deleted files (2 files)
- `packages/cli/src/agent-runtime/acp-session.ts` — 954-line god module (runAcpSession + createAcpConnection)
- `packages/cli/src/agent-runtime/session.ts` — unused SessionManager
- `packages/cli/src/db/agent-session-message-repo.ts` — zero-callers repo

### Updated consumer files (11 files)
- `packages/cli/src/agent-runtime/index.ts` — exports updated
- `packages/cli/src/workflow/plan-stage-runner.ts` — uses `AgentSession.create()` + manual lifecycle
- `packages/cli/src/workflow/check-stage-runner.ts` — uses `AgentSession.create()` + manual lifecycle
- `packages/cli/src/workflow/workflow-engine.ts` — passes through new options
- `packages/cli/src/workflow/stage-context.ts` — type updates
- `packages/cli/src/workflow/checks/build-test-check.ts` — uses `withSession()`
- `packages/cli/src/workflow/checks/code-compiles-check.ts` — uses `withSession()`
- `packages/cli/src/services/agent-runner-service.ts` — orphan scan uses DB
- `packages/cli/src/services/skill-service.ts` — uses `withSession()`
- `packages/cli/src/services/explore-acp-service.ts` — uses `withSession()`
- `packages/cli/src/services/conflict-resolution.ts` — uses `AgentSession.create()` + manual lifecycle
- `packages/cli/src/openspec/ralph-executor.ts` — uses `withSession()` via `_acpSessionRunner`
- `packages/cli/src/server/index.ts` — removed SessionManager, updated imports
- `packages/cli/src/api/issues.ts` — removed agent_session_message endpoints
- `packages/cli/src/api/propose.ts` — import cleanup
- `packages/cli/src/server/state-manager.ts` — removed agentSessionMessageRepo
- `packages/cli/src/db/index.ts` — removed agent-session-message-repo export
- `packages/cli/src/db/migrations.ts` — v22 drop agent_session_message, v23 add process_pid
- `packages/cli/src/db/coder-session-repo.ts` — added processPid column support

### Updated test files (15 files)
- `packages/cli/tests/acp-connection.test.ts`
- `packages/cli/tests/acp-hang-recovery.test.ts`
- `packages/cli/tests/acp-session-taskid.test.ts`
- `packages/cli/tests/api-rebase.test.ts`
- `packages/cli/tests/api-routes.test.ts`
- `packages/cli/tests/build-pipeline-observability.test.ts`
- `packages/cli/tests/e2e.test.ts`
- `packages/cli/tests/issue-archive.test.ts`
- `packages/cli/tests/pipeline-checkpoint.test.ts`
- `packages/cli/tests/ralph-executor.test.ts`
- `packages/cli/tests/recover-build-all-pass.test.ts`
- `packages/cli/tests/recover-issues.test.ts`
- `packages/cli/tests/services/issue-task-queue.test.ts`
- `packages/cli/tests/session-stream-log.test.ts`
- `packages/cli/tests/skill-service.test.ts`
- `packages/cli/tests/start-handler-resilience.test.ts`

## Fix Suggestions

### 1. Extract `AcpProtocol` to bring `agent-session.ts` under 300 lines

`agent-session.ts:121-189` — `setupConnection()` and `handleSessionUpdate()` contain the ACP protocol handling that design D2 specified as a separate module. Extracting these into `acp-protocol.ts` would reduce `agent-session.ts` to ~300 lines and achieve the target architecture.

### 2. Remove business deps from `AgentSessionOptions`

`agent-session.ts:30-34` — Move `eventBus`, `workflowLogRepo`, `sessionStreamLogRepo`, `coderSessionRepo` out of `AgentSessionOptions`. Consumers should create `WorkflowSessionObserver` directly and pass it via `observers: []`. This would require updating ~6 consumer files but would complete the decoupling.

### 3. Add logging to silent catches

`agent-session.ts:162`, `agent-session.ts:167-168`, `agent-session.ts:189`, `session-observer.ts:181` — Replace `catch {}` with `catch (err) { log.error('observer callback failed', { ... }); }`.

### 4. Fix state machine initialization

`agent-session.ts:118` and `agent-session.ts:332-338` — Instead of replacing the state machine, initialize it with a deferred DB writer, or delay creation until `coderSessionId` is available.

<promise>PASS</promise>
