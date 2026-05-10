# Review Report

## Result: FAIL

## Dimensions

### Correctness: FAIL

1. `GET /api/agent/session-status` cannot report `probing` or `failed` sessions.
File: `packages/cli/src/api/agent.ts:70-75`
Evidence: the route loads sessions with `coderSessionRepo.findAllRunning()`, while `findAllRunning()` only returns rows where `status = 'running'` (`packages/cli/src/db/coder-session-repo.ts:218-223`). As a result, the later branches that map `probing` to `Checking session` and `failed` to `Session failed` (`packages/cli/src/api/agent.ts:91-96`) are unreachable for persisted data.
Impact: the current-session API cannot satisfy the simplified current-session-state requirement for `Checking session` or `Session failed`.
Suggested fix: add a repository query that returns the latest relevant current session in `running`, `probing`, or `failed`, and use that query in `packages/cli/src/api/agent.ts:70-106`.

2. Persisted `probe_sent_at` does not preserve the runtime-recorded probe timestamp.
File: `packages/cli/src/services/session-observers.ts:170-180`
File: `packages/cli/src/db/coder-session-repo.ts:252-257`
Evidence: `AgentSession.startProbe()` records `_probeSentAt` and emits it through `notifyLivenessUpdate()` (`packages/cli/src/agent-runtime/agent-session.ts:222-239`). `WorkflowSessionObserver.onLivenessUpdate()` ignores `update.probeSentAt` and only passes `update.probeDeadlineAt` into `markProbing()`. `markProbing()` then writes a fresh `now` value into `probe_sent_at` instead of persisting the emitted runtime timestamp.
Impact: persisted liveness metadata can drift from the actual runtime transition time, so the stored `probeSentAt` is not the exact recorded probe timestamp.
Suggested fix: change `markProbing()` to accept both `probeSentAt` and `probeDeadlineAt`, pass both values from `packages/cli/src/services/session-observers.ts:176-177`, and persist them directly in `packages/cli/src/db/coder-session-repo.ts:252-257`.

### Complexity: FAIL

1. `AgentSession.execute()` is too large for the stated review target.
File: `packages/cli/src/agent-runtime/agent-session.ts:511-738`
Evidence: one function handles prompt execution, timeout, cancellation, quiet-threshold monitoring, probe lifecycle, and result shaping in roughly 228 lines.
Impact: this increases change risk around state transitions and makes the liveness flow harder to validate.
Suggested fix: extract the monitor loop, probe-deadline handling, and terminal-result shaping into smaller private helpers in `packages/cli/src/agent-runtime/agent-session.ts`.

### Test Coverage: FAIL

1. There is no test covering `GET /api/agent/session-status` for persisted `probing` or `failed` sessions.
File: `packages/cli/tests/api/agent-sessions.test.ts:42-312`
Evidence: the API tests cover `/api/agent/sessions`, but not `/api/agent/session-status`.
Suggested fix: add explicit tests for `Running`, `Checking session`, `Session failed`, and `No active session` in `packages/cli/tests/api/agent-sessions.test.ts` or a dedicated `session-status` test file.

2. Probe timestamp persistence tests only assert non-null, not equality with the runtime-emitted timestamps.
File: `packages/cli/tests/session-liveness-regression.test.ts:461-464`
File: `packages/cli/tests/session-liveness-regression.test.ts:525-529`
Evidence: current tests confirm `probeSentAt` and `probeDeadlineAt` are present, but they do not verify that the persisted `probeSentAt` matches the runtime-emitted value.
Suggested fix: capture the emitted liveness update and assert exact equality against the persisted row in `packages/cli/tests/session-liveness-regression.test.ts`.

Test execution evidence:
`npm test -- --run tests/session-liveness.test.ts tests/session-liveness-observer.test.ts tests/session-liveness-regression.test.ts tests/cli-session-liveness.test.ts tests/api/agent-sessions.test.ts tests/workflow/workflow-session-handling.test.ts tests/ralph-executor.test.ts`

### Security: PASS

No obvious injection, secret exposure, or unsafe input-handling issues were found in the reviewed liveness changes.
Evidence: reviewed changes use repository methods and enum-like statuses/timestamps rather than raw user-controlled SQL or shell interpolation in the affected paths.

### Spec Compliance: FAIL

1. Acceptance criterion: `session` 有任何 ACP/opencode 新数据时会更新 `lastDataAt`，并保持 `running`.
Verdict: PASS
Evidence: `handleSessionUpdate()` calls `refreshLastDataAt()` before observer fan-out (`packages/cli/src/agent-runtime/agent-session.ts:251-252`), and `refreshLastDataAt()` updates `_lastDataAt` and keeps or returns the session to `running` (`packages/cli/src/agent-runtime/agent-session.ts:171-192`). Covered by `packages/cli/tests/session-liveness-regression.test.ts:118-149`.

2. Acceptance criterion: `running` session 静默超过阈值会进入 `probing`，并向同一 opencode session 发送 probe.
Verdict: PASS
Evidence: the quiet-threshold branch calls `startProbe()` (`packages/cli/src/agent-runtime/agent-session.ts:601-605`), and `startProbe()` transitions to `probing` then calls `this._connection.prompt({ sessionId: this._sessionId, ... })` (`packages/cli/src/agent-runtime/agent-session.ts:221-249`). Covered by `packages/cli/tests/session-liveness-regression.test.ts:151-202`.

3. Acceptance criterion: `probing` 期间收到任何有效新数据会回到 `running`.
Verdict: PASS
Evidence: `refreshLastDataAt()` transitions `probing -> running`, clears probe deadline state, and emits a running liveness update (`packages/cli/src/agent-runtime/agent-session.ts:173-188`). Covered by `packages/cli/tests/session-liveness-regression.test.ts:204-251`.

4. Acceptance criterion: probe 超时、发送失败、协议断开或进程退出会让 session 进入 `failed`.
Verdict: PASS
Evidence: probe deadline timeout returns a failed session result (`packages/cli/src/agent-runtime/agent-session.ts:612-633`), and execute errors return `failureKind: 'session_failed'` (`packages/cli/src/agent-runtime/agent-session.ts:712-734`). Covered by `packages/cli/tests/session-liveness-regression.test.ts:253-301` and `packages/cli/tests/session-liveness-regression.test.ts:589-632`.

5. Acceptance criterion: `session failed` 会返回给 task/workflow 调用方.
Verdict: PASS
Evidence: `AcpSessionResult` carries `failureKind` and `failureReason` (`packages/cli/src/agent-runtime/agent-session.ts:47-55`), and the Ralph executor handles `session_failed` explicitly (`packages/cli/src/openspec/ralph-executor.ts:767-776`). Covered by `packages/cli/tests/ralph-executor.test.ts:879-936` and `packages/cli/tests/workflow/workflow-session-handling.test.ts:144-209`.

6. Acceptance criterion: `issue.stage/status` 不因 session `probing/failed` 直接改变.
Verdict: PASS
Evidence: liveness persistence updates only touch `coder_session` (`packages/cli/src/services/session-observers.ts:170-180`), and regression coverage verifies the issue remains `build/active` after probing and failure (`packages/cli/tests/session-liveness-regression.test.ts:303-349`).

7. Acceptance criterion: CLI/Web 能看到 `Running / Checking session / Session failed / No active session`.
Verdict: FAIL
Evidence: CLI mapping exists in `packages/cli/src/cli/commands/issue.ts:73-96` and is tested in `packages/cli/tests/cli-session-liveness.test.ts:16-83`. Issue detail/session detail API mapping exists in `packages/cli/src/api/issues.ts:2167-2207`. However, the dedicated current-session API cannot surface `probing` or `failed` because it only queries `findAllRunning()` (`packages/cli/src/api/agent.ts:70-75`; `packages/cli/src/db/coder-session-repo.ts:218-223`). This breaks the requirement that agent/session status data distinguish all four user-facing states.

8. Acceptance criterion: 测试覆盖有新数据不 probe、静默后 probe、probe 后新数据恢复、probe 超时失败、`session failed` 返回给 task/workflow.
Verdict: PASS
Evidence: no-probe coverage exists in `packages/cli/tests/session-liveness.test.ts:147-180`; quiet-then-probe in `packages/cli/tests/session-liveness-regression.test.ts:151-202`; probe recovery in `packages/cli/tests/session-liveness-regression.test.ts:204-251`; probe timeout failure in `packages/cli/tests/session-liveness-regression.test.ts:253-301`; session-failed propagation in `packages/cli/tests/ralph-executor.test.ts:879-936` and `packages/cli/tests/workflow/workflow-session-handling.test.ts:144-209`.

## Changed Files Coverage

Reviewed implementation evidence covered these changed areas:

1. `packages/cli/src/agent-runtime/agent-session.ts`
2. `packages/cli/src/agent-runtime/session-observer.ts`
3. `packages/cli/src/agent-runtime/session-state.ts`
4. `packages/cli/src/services/session-observers.ts`
5. `packages/cli/src/db/coder-session-repo.ts`
6. `packages/cli/src/db/migrations.ts`
7. `packages/cli/src/api/agent.ts`
8. `packages/cli/src/api/issues.ts`
9. `packages/cli/src/cli/commands/issue.ts`
10. `packages/cli/src/openspec/ralph-executor.ts`
11. `packages/cli/tests/session-liveness.test.ts`
12. `packages/cli/tests/session-liveness-observer.test.ts`
13. `packages/cli/tests/session-liveness-regression.test.ts`
14. `packages/cli/tests/cli-session-liveness.test.ts`
15. `packages/cli/tests/api/agent-sessions.test.ts`
16. `packages/cli/tests/workflow/workflow-session-handling.test.ts`
17. `packages/cli/tests/ralph-executor.test.ts`

## Overall Verdict

At least one dimension fails, so the overall verdict is FAIL.

<promise>FAIL</promise>
