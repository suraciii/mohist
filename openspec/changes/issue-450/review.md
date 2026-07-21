# Review - Issue #450 Pi Workflow Path

## Scope

Reviewed all changed files against the issue's seven acceptance criteria, the three spec files under `openspec/changes/issue-450/specs/`, the design document (`openspec/changes/issue-450/design.md`), and the documented product/design specs (`docs/actions/pi.md`, `design/runtimes/pi.md`).

## Findings

### F1. Invalid provider policy configuration still allows the Runner to claim work

**What**: `RunnerHost.initializeSharedConnection()` now calls `parseProviderErrorPolicy()` and passes a valid policy to both runtimes, but the invalid branch at `packages/runner/src/runtime/host.ts:318-320` only logs the diagnostic. It then constructs both runtimes without `providerErrorPolicy`, so they silently use their defaults and the normal readiness gate can become ready.

**Where**: `packages/runner/src/runtime/host.ts:317-330`

**Impact**: Invalid `MOHIST_PROVIDER_ERROR_PATTERNS` JSON/regex or invalid `MOHIST_PROVIDER_RETRY_THRESHOLD` does not fail before work claim. The documented operator configuration is accepted with a warning while the requested policy is ignored, and work can execute under different defaults. This violates design D2 and T-002 acceptance criterion 9, which require invalid configuration to be rejected before work claim with an actionable diagnostic.

**Severity**: Blocking — malformed operator configuration must not be silently replaced by defaults.

## Coverage

- **AC 1** (end-to-end Pi turn): `mohist/pi` is registered in `ActionRegistry`, `piAction` handles full lifecycle, `WorkExecutor` passes `piRuntime` through `baseContext`. Tests in `pi.test.ts` cover the full flow.
- **AC 2** (same-name session reuse): `sessionNameFromContext` resolves to Work ID for omitted session, `piAction` reuses `runtimeSessionId` from the opened session.
- **AC 3** (Runner restart reuse): `PiRuntime` opens sessions by exact path, `AgentSession` stores the absolute path as `runtimeSessionId`, `piAction` passes it to `runTurn`.
- **AC 4** (missing session file): `PiRuntime.runTurn` returns `missing-session` with Reset guidance on open failure, `piAction` maps to `runtime-session-missing` with Reset hint.
- **AC 5** (deadline and provider exhaustion): `PI_TURN_DURATION_MS` = 60 min, `fixAndAbort` fixes result before abort, `isRetryFailure` checks policy patterns and threshold.
- **AC 6** (project-level Pi config excluded): `sdk.ts` fixes `projectTrusted: false`, `DefaultResourceLoader` with the same `settingsManager` excludes project resources.
- **AC 7** (Session audit visibility): Pi events flow through `workflowAgentSessionRuntimeEvents`, Web chat/timeline views handle `provider.retry`, `SessionUsageSummary` shows `cachedWriteTokens`, milestone classifier recognizes `mohist/pi`.

All seven product acceptance criteria are addressed. The remaining F1 violates the design/task contract for invalid provider-policy configuration and is independent of the normal Pi turn path.

## Structural Checks

- All tests pass: Runner (101 files, 1,176 tests), Web (368 files, 4,997 tests), Server (unit 101, spec 260, binding 7), CLI (64). No generated changes remain.
- `tasks.json` is acyclic and all six tasks reference valid spec anchors.
- All Pi SDK imports are confined to `packages/runner/src/runtime/pi/`.
- The EF migration for unbounded physical IDs is a no-op (SQLite TEXT columns already support unbounded length).
- `OpenCode` and `Pi` wire changes are atomic: both adapters now pass `runtime`/`expectedRuntime`/`expectedRuntimeSessionId` in open/attach requests.
- The `WorkflowSessionTurnCoordinator` is process-local and stores no runtime binding or durable state.

## Verdict

F1 remains a blocking startup/readiness gap. The valid provider-policy path is wired, but invalid configuration falls back to defaults instead of preventing work claim.

<promise>FAIL</promise>
