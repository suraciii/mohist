## Review

Reviewed implementation across 5 tasks (T-001 through T-005) covering 31 changed files with +1489/-34 lines. Typecheck passes. All 221 tests pass (174 in core test suite, 47 in workflow tests).

### Correctness

**ERROR: Disabled verification satisfies approval guard**

`health-gate-check.ts:105` returns `status: 'pass'` when `policy.enabled` is `false`. The approval guards in `base-stage-runner.ts:439` and `domain/index.ts:990` only check `status === 'pass'` / `status === 'passed'` without checking the `enabled` field. This means `healthGates.check.enabled: false` allows Check approval to proceed, violating spec requirement "Disabled Check verification cannot satisfy approval evidence" (`workflow-config/spec.md:18-22`).

**Fix:** In `base-stage-runner.ts:439` and `domain/index.ts:990`, add a check that rejects health:check results where `output.enabled === false`:
```
// After status check, also check:
const healthCheckOutput = healthCheckResult?.output as Record<string, unknown> | undefined;
if (healthCheckOutput?.enabled === false) {
  return { ok: false, message: 'Cannot request check approval: health:check is disabled by policy' };
}
```

### Complexity

All modified and new functions stay under 50 lines. The API guard in `issues.ts` (~75 lines) is the longest new block but is a linear sequence of independent validation checks with early returns — cyclomatic complexity remains low.

### Test Coverage

- New `check-verification-regression.test.ts` (526 lines) covers all T-005 acceptance criteria with 14 tests
- All existing tests updated to include `health:check` pass step, proving backward compatibility
- Regression tests prove: failure blocks AI review/merge-ready/approval, passing evidence precedes review-passed/merge-ready, config compatibility, and failure diagnostics

### Security

No injection risks. Git SHA resolution (`resolveHeadSha`) uses `execFile` with explicit args array. Log excerpt truncation bounds output at 5000 chars. No secrets exposed.

### Spec Compliance

| Acceptance Criterion | Verdict | Evidence |
|---|---|---|
| Default Check execution runs full verification before AI review | **PASS** | `check-stage-runner.ts:62-74` registers `HealthGateCheck` as pre-task check; `base-stage-runner.ts:200-207` stops on pre-task failure before `executeTasks()` |
| Full verification failure blocks AI review, merge-ready, approval | **PASS** | `base-stage-runner.ts:200-206` returns early on pre-task failure; tests in `check-verification-regression.test.ts:116-178` prove ai-review and merge-ready never run |
| Passing verification persisted as `health:check` | **PASS** | `domain/index.ts:486` adds `health:check` to Check stage definition; `workflow-run-projection.ts:140` includes it in projection |
| Persisted evidence includes command, status, duration, summary, log excerpt | **PASS** | `health-gate-check.ts:140-148` (pass) and `health-gate-check.ts:175-193` (fail) include all fields |
| Evidence bound to candidate implementation | **PASS** | `health-gate-check.ts:100` collects `candidateHeadSha` via `resolveHeadSha()`; API guard at `issues.ts:2267-2277` validates SHA matches |
| Candidate change invalidates evidence | **PASS** | `domain/index.ts:641,652,828` reset `health:check` alongside `review-passed` and `merge-ready` on candidate change |
| Check approval requires verification + review + merge-ready | **PASS** | `base-stage-runner.ts:438-446` and `base-stage-runner.ts:525-531` require `health:check` pass; `domain/index.ts:990-994` requires it in domain path |
| Missing/failed verification blocks approval, shows reason | **PASS** | `base-stage-runner.ts:440-444` returns descriptive message; Web UI red banner at `PipelineView.tsx:813-816` |
| `mo issue show` exposes failed Check health gate | **PASS** | `issue.ts:479` allows `health:check` through filter; displays command/summary/duration/log excerpt at `issue.ts:489-500` |
| `checks.buildTest` config compatibility | **PASS** | `check-verification-regression.test.ts:338-410` tests all config paths; `workflow-loader` compatibility mapping tested |
| Disabled verification cannot satisfy approval evidence | **FAIL** | `health-gate-check.ts:105` returns `status: 'pass'` when disabled; approval guards only check status, not `enabled` field |
| Web UI approval panel indicates verified candidate | **WARN** | No explicit "verification passed" indicator in approval panel; relies on absence of failure banner |
| CLI shows missing verification blocks approval | **WARN** | No explicit message for "verification never ran" case; relies on absence of `health:check` in stage checks output |

### Warnings

1. **Web UI no verified-candidate indicator** (`PipelineView.tsx`): Spec requires the approval panel to "indicate that required full verification evidence passed for the approval candidate." The implementation shows a failure banner when verification fails but doesn't add a positive indicator when approval is available with passing verification.

2. **CLI no explicit missing-verification message** (`issue.ts`): Spec requires showing "approval is blocked by missing Check verification evidence" when verification hasn't run. The implementation relies on implicit absence of `health:check` in the checks list rather than an explicit blocking message.

3. **Domain `checkFailurePolicies` inconsistency** (`domain/index.ts:493`): The Check stage definition includes `{ checkName: 'health:check', fixTaskId: 'fix-check-health', maxAttempts: 1 }`, but `CheckStageRunner.getCheckFailurePolicies()` returns `[]` and neither `runFixTask` nor `executeReportedTask` handles `fix-check-health`. The domain declares auto-fix policy that the runner silently ignores.

4. **`checkedAt` uses `new Date().toISOString()`** (`domain/index.ts:1017`): The extraction timestamp is the current wall clock rather than when the check actually ran, which may drift from the real execution time.

### Fix Suggestions

**ERROR fix — disabled verification must not satisfy approval:**

`packages/cli/src/workflow/base-stage-runner.ts:438-446` — After the status check, add:
```typescript
const healthCheckOutput = healthCheckResult?.output as Record<string, unknown> | undefined;
if (healthCheckOutput?.enabled === false) {
  return { ok: false, message: 'Cannot request check approval: health:check is disabled by policy and cannot serve as approval evidence' };
}
```

`packages/cli/src/workflow/domain/index.ts:990` — After the status check, add:
```typescript
const healthCheckOutput = healthCheck.output as Record<string, unknown> | undefined;
if (healthCheckOutput?.enabled === false) {
  return { error: 'Cannot request check approval: health:check is disabled by policy and cannot serve as approval evidence' };
}
```

`packages/cli/src/api/issues.ts:2217-2221` — After the health check status validation, add:
```typescript
if (latestHealthCheckOutput?.enabled === false) {
  return c.json({ success: false, error: 'Cannot approve: health:check is disabled by policy. Enable Check verification or re-run with verification enabled.' } satisfies ApiResponse, 409);
}
```

<promise>FAIL</promise>
