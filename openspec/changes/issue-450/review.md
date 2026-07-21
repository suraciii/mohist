# Review - Issue #450 Pi Workflow Path

## Scope

Reviewed all changed files against the issue's seven acceptance criteria, the three spec files under `openspec/changes/issue-450/specs/`, the design document (`openspec/changes/issue-450/design.md`), and the documented product/design specs (`docs/actions/pi.md`, `design/runtimes/pi.md`).

## Findings

### F1. `parseProviderErrorPolicy` is not wired into the Runner composition root

**What**: The `parseProviderErrorPolicy` function in `packages/runner/src/runtime/pi/policy.ts` is implemented, tested, and exported from the public index, and `MOHIST_PROVIDER_ERROR_PATTERNS` / `MOHIST_PROVIDER_RETRY_THRESHOLD` are documented in `docs/self-host.md`. However, `RunnerHost.init()` (`packages/runner/src/runtime/host.ts:325`) creates `PiRuntime` with only `{ agentDir: this.options.runnerRoot }` — no `providerErrorPolicy` parameter. `OpenCodeRuntime` is also created without a shared policy (line 318). The `parseProviderErrorPolicy` function is never called in production code.

**Where**: `packages/runner/src/runtime/host.ts:318,325` vs `packages/runner/src/runtime/pi/policy.ts:21`

**Impact**: `MOHIST_PROVIDER_ERROR_PATTERNS` and `MOHIST_PROVIDER_RETRY_THRESHOLD` have no effect — Pi always uses `DEFAULT_PI_PROVIDER_ERROR_POLICY` with the hardcoded defaults and threshold 5. OpenCode does not receive the shared policy either. This contradicts the design (D2): "The Runner composition root parses... passes that same instance to OpenCode and Pi" and the documented self-host settings.

**Severity**: Functional — the documented operator settings are inert.

### F2. `piAction` does not attempt `session.closed` reporting when `runTurn` throws unexpectedly

**What**: The `piAction`'s try/catch around `runtime.runTurn()` at `packages/runner/src/actions/pi.ts:84-88` catches any unexpected exception from `runTurn` and returns `turn-failed` immediately without attempting to report `session.closed` via the dedicated 30-second terminal signal. `runTurn` itself catches most errors internally, but errors from the observer callback or unexpected SDK failures could bypass the internal catch and leave the session without a terminal close event.

**Where**: `packages/runner/src/actions/pi.ts:84-88`

**Impact**: If `runTurn` throws an unexpected exception after prompt submission, the session is not marked closed. The AgentSession transcript would have no `session.closed` event for that turn, affecting audit completeness.

**Severity**: Edge case — `runTurn` handles the expected failure modes internally, so this path is unlikely to be hit in practice.

### F3. `piAction` does not inspect the returned acceptance entries from `workflowAgentSessionRuntimeEvents`

**What**: The `report` closure in `packages/runner/src/actions/pi.ts:67-73` calls `connection.workflowAgentSessionRuntimeEvents` but does not use the returned `AgentSessionRuntimeEventAcceptance[]`. The `connection.ts` code validates the response internally (checks HTTP status, JSON parse, array shape, and count match) and throws on any failure. The acceptance entries are discarded.

**Where**: `packages/runner/src/actions/pi.ts:67-73`

**Impact**: No functional correctness issue — all validation is done inside `connection.ts`. The return value is unused but the error paths are correctly handled through exceptions.

**Severity**: Informational — no fix needed, but the return value being discarded is a missed opportunity for explicit validation at the caller level.

## Coverage

- **AC 1** (end-to-end Pi turn): `mohist/pi` is registered in `ActionRegistry`, `piAction` handles full lifecycle, `WorkExecutor` passes `piRuntime` through `baseContext`. Tests in `pi.test.ts` cover the full flow.
- **AC 2** (same-name session reuse): `sessionNameFromContext` resolves to Work ID for omitted session, `piAction` reuses `runtimeSessionId` from the opened session.
- **AC 3** (Runner restart reuse): `PiRuntime` opens sessions by exact path, `AgentSession` stores the absolute path as `runtimeSessionId`, `piAction` passes it to `runTurn`.
- **AC 4** (missing session file): `PiRuntime.runTurn` returns `missing-session` with Reset guidance on open failure, `piAction` maps to `runtime-session-missing` with Reset hint.
- **AC 5** (deadline and provider exhaustion): `PI_TURN_DURATION_MS` = 60 min, `fixAndAbort` fixes result before abort, `isRetryFailure` checks policy patterns and threshold.
- **AC 6** (project-level Pi config excluded): `sdk.ts` fixes `projectTrusted: false`, `DefaultResourceLoader` with the same `settingsManager` excludes project resources.
- **AC 7** (Session audit visibility): Pi events flow through `workflowAgentSessionRuntimeEvents`, Web chat/timeline views handle `provider.retry`, `SessionUsageSummary` shows `cachedWriteTokens`, milestone classifier recognizes `mohist/pi`.

All seven acceptance criteria are addressed by the implementation with the exception of F1 (provider policy wiring) which weakens the coverage of AC 5's configurable provider error handling.

## Structural Checks

- All tests pass: Runner (101 files, 1,176 tests), Web (368 files, 4,997 tests), Server (unit 101, spec 260, binding 7), CLI (64). No generated changes remain.
- `tasks.json` is acyclic and all six tasks reference valid spec anchors.
- All Pi SDK imports are confined to `packages/runner/src/runtime/pi/`.
- The EF migration for unbounded physical IDs is a no-op (SQLite TEXT columns already support unbounded length).
- `OpenCode` and `Pi` wire changes are atomic: both adapters now pass `runtime`/`expectedRuntime`/`expectedRuntimeSessionId` in open/attach requests.
- The `WorkflowSessionTurnCoordinator` is process-local and stores no runtime binding or durable state.

## Verdict

F1 is a functional gap: the operator-facing `MOHIST_PROVIDER_ERROR_PATTERNS` and `MOHIST_PROVIDER_RETRY_THRESHOLD` settings documented in `docs/self-host.md` are not wired into the Runner composition root. F2 and F3 are informational edge cases.

<promise>FAIL</promise>