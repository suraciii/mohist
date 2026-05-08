# Review Report

## Result: PASS

Implementation successfully cleans the AgentSession runtime boundary. `AgentSession` no longer imports or constructs workflow visibility adapters. All consumers create observers through `createWorkflowSessionObservers`. All 83 test files pass (1417 tests). Three warnings noted but none are errors.

---

## Dimensions

### Correctness — PASS

No bugs found. Observer notification pattern is consistently try-caught. State transitions use `SessionStateMachine` with valid transition guards.

**Pre-existing concern** (not introduced by this refactor): `close()` always tries `transition('completed')` (`agent-session.ts:474`), which throws and is caught if the session already transitioned to `failed`/`timeout`/`cancelled`. Handled by try/catch + log.warn.

### Complexity — PASS

- `AgentSession.create()` at ~117 lines (222-339) — longest method, acceptable given multi-step ACP init protocol
- `handleSessionUpdate()` at ~63 lines (144-207) — within limits
- `WorkflowSessionObserver` at ~180 lines — clean adapter with focused methods
- `createWorkflowSessionObservers()` at 15 lines — straightforward factory
- All functions under 50 lines. No cyclomatic complexity concerns.

### Test Coverage — PASS

- **28 tests** in `agent-session-boundary.test.ts`: boundary types, text chunks, tool calls, stream logs, workflow logs, coder session status, raw notification bridge, multi-round session reuse, lifecycle notifications, withSession cleanup, abort path, timeout path, model override
- **1417 total tests pass** across 83 test files — zero regressions

### Security — PASS

No secrets exposed, no injection risks. Observer callbacks consistently isolated with try/catch.

### Spec Compliance — PASS

#### agent-runtime/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| `AgentSessionOptions` excludes `EventBus`, `WorkflowLogRepo`, `SessionStreamLogRepo`, `CoderSessionRepo` | **PASS** | `agent-session.ts:17-35` — none of these fields exist |
| `AgentSessionOptions` accepts `observers?: SessionObserver[]` | **PASS** | `agent-session.ts:29` |
| Agent runtime has no workflow visibility imports | **PASS (warning)** | `session-observer.ts:35-37` re-exports visibility symbols — soft boundary leak via barrel. `agent-session.ts` itself is clean |
| `agent-session.ts` does not import `WorkflowSessionObserver` | **PASS** | Confirmed — imports only `SessionObserver`, `SessionContext`, `SessionState`, `ToolCallEvent` from `./session-observer` |
| Session events published through observers | **PASS** | `agent-session.ts:164-206` — all events via `this._observers` with try/catch per observer |
| Observer failures logged without stopping session flow | **PASS** | Every observer call in `agent-session.ts` wrapped in try/catch with error logging |
| `withSession` guarantees cleanup | **PASS** | `agent-session.ts:508-513` — `finally { await session.close(); }` |
| Abort performs cancel + onBeforeKill + cleanup + user-visible failure | **PASS** | `agent-session.ts:368-388` + test `agent-session-boundary.test.ts:667-695` |
| Timeout performs cancel + onBeforeKill + cleanup + terminal state + failure | **PASS** | `agent-session.ts:390-420` + test `agent-session-boundary.test.ts:741-770` |
| Model override degrades on failure | **PASS** | `agent-session.ts:313-325` — catch logs warning, clears model. Test `agent-session-boundary.test.ts:834-853` |
| No shallow ACP protocol layer required | **PASS** | No `AcpDriver` introduced. ACP SDK calls remain in `AgentSession` — acceptable per spec |

#### coder-session-tracking/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Coder session row created through observer | **PASS** | `session-observers.ts:51-71` — `onSessionStart` creates row. `AgentSession` has no `CoderSessionRepo` import |
| Terminal session status updated through observer | **PASS** | `session-observers.ts:156-166` — `onStateChange` updates for completed/failed/timeout/cancelled |
| Text chunk issue identity preserved | **PASS** | `session-observers.ts:92-106` — `sseIssueId` uses `String(ctx.issueNumber ?? ctx.issueId ?? '')` |
| Tool call payload complete and deduplicable | **PASS** | `agent-session.ts:185-206` — `ToolCallEvent` with stable `toolCallId` from `ToolCallIdGenerator` |

#### pipeline-session-events/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Plan stage reuses multi-round session | **PASS** | `plan-stage-runner.ts:138` — single `AgentSession.create()`, loop at line 195. Test `agent-session-boundary.test.ts:506-534` confirms same `acpSessionId` across 3 rounds |
| Check stage reuses multi-round session | **PASS** | `check-stage-runner.ts:132` — single `AgentSession.create()`, loop at line 192 |
| Plan raw notifications emit `plan_session_update` | **PASS** | `plan-stage-runner.ts:95-113` — `planBridgeObserver` via `eventBus.emit('plan_session_update', ...)` |
| Check raw notifications emit `plan_session_update` | **PASS** | `check-stage-runner.ts:92-110` — same bridge pattern |
| Bridge emission is fire-and-forget | **PASS** | Both bridges wrap in try/catch with log.warn |

#### workflow-log/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Workflow logs written by observer | **PASS** | `session-observers.ts:143-149` — `onSessionEvent` writes to `workflowLogRepo`. `AgentSession` has no `WorkflowLogRepo` import |
| Session stream logs written by observer | **PASS** | `session-observers.ts:136-142` — stream events to `sessionStreamLogRepo`. `AgentSession` has no `SessionStreamLogRepo` import |
| Observer preserves payload compatibility | **PASS** | `onSessionEvent` passes `ctx.issueId`, `ctx.acpSessionId`, `eventType`, `update` — same shapes as before |

---

## Warnings

### W1: `session-observer.ts` re-exports workflow visibility symbols

**Severity**: Warning
**File**: `agent-runtime/session-observer.ts:35-37`
**Detail**: Barrel re-exports `WorkflowSessionObserver`, `WorkflowSessionObserverDeps`, `createWorkflowSessionObservers` from `../services/session-observers`. `agent-session.ts` itself has clean imports, but `agent-runtime/` package acts as a conduit for visibility-layer types. Design D3 states these should move out or split.

### W2: `onSessionUpdate` remains in `AgentSessionOptions`

**Severity**: Warning
**File**: `agent-session.ts:26`
**Detail**: Design D1 explicitly lists `onSessionUpdate` for removal. It is still present and internally converted to `SessionObserver` via `buildRawNotificationObserver()` at lines 89-93. This leaves an ACP-specific callback in runtime options, competing with the observer interface.

### W3: `WorkflowSessionObserver.nextToolCallId()` is dead code

**Severity**: Warning
**File**: `session-observers.ts:171-190`
**Detail**: `ToolCallIdGenerator` in `agent-session.ts:62-87` now owns ID generation. The observer's `nextToolCallId()` method, `coderToolCallCounter`, and `coderToolCallIds` are never called — ~20 lines of dead code from before the refactor.

---

## Fix Suggestions

### Fix 1: Remove re-exports from `session-observer.ts`

**File**: `agent-runtime/session-observer.ts:35-37`
Remove the three re-export lines. Keep them only in `agent-runtime/index.ts` if consumer convenience is desired, or move consumers to import from `../services/session-observers` directly.

### Fix 2: Remove `onSessionUpdate` from `AgentSessionOptions`

**File**: `agent-runtime/agent-session.ts:26`, `agent-runtime/agent-session.ts:89-93`, `agent-runtime/agent-session.ts:231-233`
Remove the `onSessionUpdate` field, the `buildRawNotificationObserver` function, and the conversion logic in `create()`. Migrate callers to pass an explicit observer.

### Fix 3: Remove dead `nextToolCallId` from `WorkflowSessionObserver`

**File**: `services/session-observers.ts:32-33,171-190`
Remove `coderToolCallCounter`, `coderToolCallIds` fields and the `nextToolCallId()` method.

---

## Acceptance Criteria Checklist

- [x] `AgentSessionOptions` contains no `EventBus` or DB repo types
- [x] `agent-runtime/agent-session.ts` does not import `WorkflowSessionObserver`
- [x] Agent Runtime runtime code does not import workflow/db/service visibility layers
- [ ] **WARNING**: `session-observer.ts` re-exports workflow visibility symbols — soft boundary leak
- [x] Workflow/service consumers create `WorkflowSessionObserver` through factory
- [x] `AgentSession` receives `SessionObserver[]` and publishes events
- [x] No ACP adapter introduced (acceptable per spec)
- [x] Plan stage reuses one multi-round session
- [x] Check stage reuses one multi-round session
- [x] `withSession` guarantees cleanup through `finally close`
- [x] Abort/timeout paths perform cancel, onBeforeKill, cleanup, and return failure
- [x] Model override applies; failure is degraded behavior
- [x] Stream logs, coder session status, and realtime events update through observers
- [x] All tests pass (83 files, 1417 passed)
- [x] No functional regression in Plan, Build, Check, Explore, Skill, and conflict-resolution flows

<promise>PASS</promise>
