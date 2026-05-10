# Review Report

## Result: FAIL

## Dimensions

### Correctness: FAIL

1. `withSession()` can overwrite failed/cancelled/timed-out sessions with a synthetic `completed` close path.
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:834-848`, `packages/cli/src/agent-runtime/agent-session.ts:873-877`
Why this fails: `close()` always attempts `transition('completed')` and emits `onStateChange(..., 'running', 'completed')`. `withSession()` always calls `session.close()` in `finally`, so a session that already returned `failed`, `timeout`, or `cancelled` can emit an incorrect completion lifecycle after the real terminal result.
Suggested fix: Guard `withSession()` so it only calls `close()` for still-active sessions, or make `close()` a no-op for terminal states other than active `running`/`probing`.

2. Probe send failure is not handled immediately.
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:504-511`
Why this fails: `startProbe()` sends a probe with `this._connection.prompt(...).catch(...)` and only logs on rejection. It does not transition to `failed` or return a failed result at probe-send time.
Suggested fix: Await the probe send or race its rejection into the main execution loop, then fail the session immediately with a specific `failureReason`.

3. `timeout` is still persisted as a session status even though the accepted state set excludes it.
Evidence: `packages/cli/src/agent-runtime/session-observer.ts:41`, `packages/cli/src/agent-runtime/agent-session.ts:452-481`, `packages/cli/src/services/session-observers.ts:161-163`, `packages/cli/src/db/coder-session-repo.ts:168`
Why this fails: runtime transitions to `timeout`, observers persist `timeout`, and repository code treats it as a stored terminal status.
Suggested fix: Persist `failed` for timeout outcomes and carry timeout detail in `failureKind`/`failureReason` instead of `coder_session.status`.

### Complexity: PASS

Evidence: The refactor extracts helper methods such as `createAbortPromise()`, `createProbeDeadlinePromise()`, `handleQuietThreshold()`, `waitForPromptProgress()`, `monitorPromptExecution()`, and `handleExecuteError()` in `packages/cli/src/agent-runtime/agent-session.ts:273-394`, keeping the main `execute()` path shorter and readable.
Residual note: `monitorPromptExecution()` remains loop-driven but stays within reviewable complexity.

### Test Coverage: FAIL

Evidence: Relevant tests exist in `packages/cli/tests/session-liveness-regression.test.ts`, `packages/cli/tests/session-liveness.test.ts`, `packages/cli/tests/workflow/workflow-session-handling.test.ts`, and `packages/cli/tests/ralph-executor.test.ts`.
Why this fails: the existing probe-send-failure test only verifies eventual failure after waiting for probe timeout rather than immediate failure on send rejection: `packages/cli/tests/session-liveness-regression.test.ts:601-644`.
Verification run: `npx vitest run packages/cli/tests/session-liveness-regression.test.ts packages/cli/tests/session-liveness.test.ts`
Observed output: tests passed, but runtime logs showed `Invalid session state transition: failed → completed`, confirming an untested correctness gap.
Suggested fix: Add an assertion that probe send rejection transitions directly to `failed` without waiting for `probeTimeoutMs`, and add a `withSession()` regression test that terminal failure is not followed by `completed`.

### Security: PASS

Evidence: No new input parsing, credential handling, or command execution surface was introduced in the reviewed changes. ACP subprocess environment filtering remains in `packages/cli/src/agent-runtime/acp-process.ts:33-39`.
Residual note: The probe text is fixed and not user-controlled, so no new injection path is introduced here.

### Spec Compliance: FAIL

1. Acceptance criterion: session 有任何 ACP/opencode 新数据时会更新 `lastDataAt`，并保持 `running`.
Verdict: PASS
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:178-199`, `packages/cli/tests/session-liveness-regression.test.ts:118-149`

2. Acceptance criterion: quiet running session 超过阈值进入 `probing` 并向同一 session 发送 probe.
Verdict: PASS
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:308-317`, `packages/cli/src/agent-runtime/agent-session.ts:484-503`, `packages/cli/tests/session-liveness-regression.test.ts:151-202`

3. Acceptance criterion: probing 期间收到任何有效新数据会回到 `running`.
Verdict: PASS
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:180-195`, `packages/cli/tests/session-liveness-regression.test.ts:204-251`

4. Acceptance criterion: probe 超时、发送失败、协议断开或进程退出会让 session 进入 `failed`.
Verdict: FAIL
Evidence: probe timeout failure exists in `packages/cli/src/agent-runtime/agent-session.ts:396-418`, but probe send failure is only logged in `packages/cli/src/agent-runtime/agent-session.ts:506-511` and does not fail immediately. This misses the explicit send-failure branch.

5. Acceptance criterion: `session failed` 会返回给 task/workflow 调用方.
Verdict: PASS
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:393`, `packages/cli/src/agent-runtime/agent-session.ts:413-418`, `packages/cli/tests/workflow/workflow-session-handling.test.ts:137-176`

6. Acceptance criterion: `issue.stage/status` 不因 session probing/failed 直接改变.
Verdict: PASS
Evidence: `packages/cli/tests/session-liveness-regression.test.ts:303-349`

7. Acceptance criterion: CLI/Web 能看到 `Running` / `Checking session` / `Session failed` / `No active session`.
Verdict: PASS
Evidence: `packages/cli/src/cli/commands/issue.ts:73-96`, `packages/cli/src/api/agent.ts:35-40`, `packages/cli/src/api/issues.ts:2167-2172`, `packages/cli/web/src/components/SessionHeader.tsx:52-106`, `packages/cli/web/src/components/SessionPage.tsx:67-89,259-263`

8. Acceptance criterion: 测试覆盖新数据不 probe、静默后 probe、probe 后新数据恢复、probe 超时失败、session failed 返回给 task/workflow.
Verdict: FAIL
Evidence: most scenarios are covered in `packages/cli/tests/session-liveness-regression.test.ts:118-349` and workflow handling tests, but the probe-send-failure case is under-specified and does not catch the spec deviation described above: `packages/cli/tests/session-liveness-regression.test.ts:601-644`.

9. Requirement `REQ-AR-001 Session liveness probing`.
Verdict: FAIL
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:506-511`

10. Requirement `REQ-CLI-001 CLI shows simplified current session state`.
Verdict: PASS
Evidence: `packages/cli/src/cli/commands/issue.ts:73-96`

11. Requirement `REQ-CST-001 Coder sessions persist liveness fields`.
Verdict: FAIL
Evidence: failed terminal status can be followed by close-time `completed` signaling in `packages/cli/src/agent-runtime/agent-session.ts:834-848`, `packages/cli/src/agent-runtime/agent-session.ts:873-877`

12. Requirement `REQ-CST-002 Coder session status remains a session-call state`.
Verdict: FAIL
Evidence: persisted `timeout` status path remains in `packages/cli/src/services/session-observers.ts:161-163`, `packages/cli/src/db/coder-session-repo.ts:168`

13. Requirement `REQ-API-001 API exposes current session liveness data`.
Verdict: PASS
Evidence: `packages/cli/src/api/issues.ts:2193-2207`, `packages/cli/src/api/agent.ts:88-97`

14. Requirement `REQ-PSE-001 Session liveness status is emitted to live clients`.
Verdict: PASS
Evidence: `packages/cli/src/services/session-observers.ts:185-198`, `packages/cli/tests/session-liveness-observer.test.ts:139-181`

15. Requirement `REQ-RTE-001 Task attempts consume session failure results`.
Verdict: PASS
Evidence: `packages/cli/tests/ralph-executor.test.ts:879-964`, `packages/cli/tests/workflow/workflow-session-handling.test.ts:137-176`

16. Requirement `REQ-WUI-001 Web UI shows simplified current session state`.
Verdict: PASS
Evidence: `packages/cli/web/src/components/SessionHeader.tsx:52-106`, `packages/cli/web/src/components/SessionPage.tsx:67-89,259-263`

17. Requirement `REQ-WA-001 Workflow consumes session results without judging liveness`.
Verdict: PASS
Evidence: `packages/cli/tests/workflow/workflow-session-handling.test.ts:137-176,383-393`

## Changed Files Covered

Reviewed changed implementation and test files:

1. `packages/cli/src/agent-runtime/agent-session.ts`
2. `packages/cli/tests/session-liveness-regression.test.ts`
3. `packages/cli/tests/api/agent-sessions.test.ts`

Reviewed related supporting files for completeness and evidence:

1. `packages/cli/src/agent-runtime/session-state.ts`
2. `packages/cli/src/agent-runtime/session-observer.ts`
3. `packages/cli/src/services/session-observers.ts`
4. `packages/cli/src/db/coder-session-repo.ts`
5. `packages/cli/src/api/agent.ts`
6. `packages/cli/src/api/issues.ts`
7. `packages/cli/src/cli/commands/issue.ts`
8. `packages/cli/src/agent-runtime/acp-process.ts`
9. `packages/cli/tests/session-liveness.test.ts`
10. `packages/cli/tests/session-liveness-observer.test.ts`
11. `packages/cli/tests/workflow/workflow-session-handling.test.ts`
12. `packages/cli/tests/ralph-executor.test.ts`
13. `packages/cli/web/src/components/SessionHeader.tsx`
14. `packages/cli/web/src/components/SessionPage.tsx`

Non-implementation worktree changes not relevant to this review verdict:

1. `.opencode/package-lock.json`
2. prior `review.md` / `review-self-check.md` deletions

## Overall Consistency Check

Overall verdict is `FAIL`, which is consistent with dimension failures in Correctness, Test Coverage, and Spec Compliance.

<promise>FAIL</promise>
