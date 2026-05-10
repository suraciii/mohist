# Review Report

## Result: PASS

## Dimensions

### Correctness: PASS

The implementation preserves terminal failed/cancelled states, handles probe-send rejection immediately, stores timeout outcomes as failed session state with timeout detail in failure metadata, refreshes liveness on session updates and successful ACP protocol responses, and removes misleading transcript status taxonomy from the session detail API.

### Complexity: PASS

The liveness execution path is split into focused helpers for timeout, abort, probe deadline, probe send failure, quiet-threshold monitoring, and terminal result shaping. The latest changes are localized and do not add new branching complexity outside the existing session runtime.

### Test Coverage: PASS

Covered by targeted tests for session liveness, observer persistence, API session status labels, transcript metadata, timeout cleanup, probe send failure, process-exit failure reason preservation, and workflow consumption of session failure results.

Verified locally:

```text
npm test -- --run tests/session-liveness-regression.test.ts tests/api/session-transcript.test.ts tests/session-liveness.test.ts tests/session-liveness-observer.test.ts tests/agent-session-boundary.test.ts
npm run typecheck
```

### Security: PASS

No new user-controlled shell construction, secret exposure, credential handling, or injection surface is introduced. The change updates session runtime state handling and API metadata only.

### Spec Compliance: PASS

1. PASS: ACP/opencode data updates `lastDataAt` and keeps the session running. Evidence: session updates call `refreshLastDataAt()` and successful ACP initialize/newSession/model/prompt responses refresh liveness; regression coverage verifies persisted `lastDataAt` advances without session updates.
2. PASS: quiet running sessions enter `probing` and send a probe to the same session. Evidence: quiet threshold monitoring calls the same-session probe path with `sessionId: this._sessionId`.
3. PASS: valid data during probing returns the session to `running`. Evidence: `refreshLastDataAt()` clears probe state and emits the `probing -> running` transition.
4. PASS: probe timeout, probe send failure, protocol/process failure, and timeout paths mark the session failed. Evidence: dedicated handlers return failed session results with concrete failure metadata.
5. PASS: session failure is returned to task/workflow callers. Evidence: `AcpSessionResult` carries `failureKind`/`failureReason`, and workflow tests cover task failure consumption.
6. PASS: issue stage/status are not mutated by probing or failed session liveness. Evidence: liveness observers update `coder_session`, not issue state.
7. PASS: CLI/API/Web can expose the simplified states `Running`, `Checking session`, `Session failed`, and `No active session` without leaking internal health taxonomy.
8. PASS: tests cover no-probe-on-data, quiet-to-probe, probe recovery, probe timeout, probe send failure, process-exit failure reason, transcript status simplification, and workflow failure propagation.

## Changed Files Covered

1. `packages/cli/src/agent-runtime/agent-session.ts`
2. `packages/cli/src/api/issues.ts`
3. `packages/cli/src/services/session-transcript-service.ts`
4. `packages/cli/tests/api/session-transcript.test.ts`
5. `packages/cli/tests/session-liveness-regression.test.ts`
6. `packages/cli/tests/session-liveness.test.ts`
7. `packages/cli/tests/agent-session-boundary.test.ts`

## Fix Suggestions

No blocking fix suggestions remain.

<promise>PASS</promise>
