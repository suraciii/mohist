## Review: Issue #225 — Check Full Verification Evidence Before Approval

### Overall Assessment

The implementation correctly addresses the core problem: Check approval was reachable without machine-verified evidence that the candidate implementation passes the configured build/test command. The fix is layered across domain model, runner orchestration, API guards, and user surfaces with good defense-in-depth.

Build passes. All 167 test files pass (2920 tests, 6 skipped).

---

### Correctness

**PASS with warnings.**

1. **Pre-task check placement is correct.** `HealthGateCheck` is registered in `getPreTaskChecks()`, which runs before `executeTasks()` (AI review). A failing `health:check` prevents task execution and later post-task checks (`review-passed`, `merge-ready`, `user-approval`). This matches spec requirement "Verification runs before AI review." (`packages/cli/src/workflow/check-stage-runner.ts:62-74`)

2. **Disabled health gate returns `status: 'pass'` with `enabled: false`.** This is caught by a second guard in `prepareApproval` at `base-stage-runner.ts:448-449` which rejects approval when `enabled === false`. Good defense-in-depth. However, the domain-level `WorkflowRun.buildApprovalOutput` at `domain/index.ts:492-494` only checks `healthCheck.status !== 'passed'` — it does NOT check `enabled === false`. This means if someone bypasses the runner and uses the domain aggregate directly, a disabled check could satisfy the domain approval path.
   - **Warning:** Inconsistent disabled-check handling between `base-stage-runner.ts:448` and `domain/index.ts:492`. The runner catches it but the domain model does not inspect the `enabled` flag.

3. **Stale evidence invalidation is correct.** `WorkflowRun.applyRebase()` now resets `health:check` alongside `review-passed` and `merge-ready`, and marks `staleEvidenceDetected`. The `approveStage()` domain method rejects when `staleEvidenceDetected` is true. (`domain/index.ts:660-668, 743-745`)

4. **Approval API guard is thorough.** The approve endpoint validates: review verdict, snapshot SHA match, merge-ready snapshot completeness and field types, `canMerge` flag, current HEAD vs snapshot, worktree cleanliness, merge-ready SHA freshness against live Git state, `health:check` pass, verification evidence presence and shape, candidate head SHA match between verification and merge-ready, check name validation. (`packages/cli/src/api/issues.ts:2022-2280`)

5. **`reopenForRepair()` resets status, failure, and approval.** This is used for approval-rejected retry and correctly resets the stage to running state. (`domain/index.ts:384-388`)

6. **`getPreTaskChecks()` returns empty when custom checks are provided** (`!this.usesDefaultChecks`). This preserves backward compatibility for tests that inject their own checks. (`check-stage-runner.ts:63`)

7. **`getCheckFailurePolicies()` returns empty array.** The old hardcoded policies for `review-passed` and `merge-ready` are removed. This means `health:check` failures will NOT trigger auto-fix through the runner's failure policy loop. The domain model still has `health:check` in `checkFailurePolicies` on `DEFAULT_STAGE_DEFINITIONS`, but since the runner returns `[]`, the domain policy won't be applied at the runner level. This appears intentional per the design ("If no safe Check-specific repair task exists, failure should remain a failed approval-blocking check").

### Complexity

**PASS with warnings.**

1. **`packages/cli/src/api/issues.ts` is very large.** The approve handler (~260 lines of validation) and the drift computation function (~180 lines) make this file extremely long. The `computeDriftStateForIssue` function and `buildDriftResponse` are not directly related to the #225 issue scope — they appear to be drift/rebase features that were committed alongside. However, they are supporting the stale evidence detection which IS in scope.

2. **The approve endpoint has deeply nested validation logic.** The sequential guard chain in the approve handler (`issues.ts:2022-2280`) is ~260 lines of flat `if/return` blocks. While individually simple, the overall function exceeds 50-line guidance. Consider extracting validation into a dedicated `validateCheckApprovalEvidence()` function.

3. **`base-stage-runner.ts:buildApprovalOutput`** is 76 lines. Acceptable but on the boundary.

4. Most functions are under 50 lines. The `HealthGateCheck.run()` method at 97 lines is within reason given its branching nature (disabled/pass/fail).

### Test Coverage

**PASS.**

Regression tests in `check-verification-regression.test.ts` (527 lines) cover:
- health:check failure blocks approval request (line 180)
- health:check failure blocks ai-review execution (line 203)
- health:check failure blocks merge-ready execution (line 217)
- Passing evidence ordering — health:check appears first (line 230)
- Evidence includes command, status, duration, summary (line 253)
- Missing verification evidence in approval output (line 300)
- Stale verification evidence rejection (line 322)
- `checks.buildTest` compatibility mapping (line 362)
- `healthGates.check` precedence over `checks.buildTest` (line 393)
- Failing evidence includes command, exit code, duration, summary, log excerpt (line 434, 470)
- Timeout evidence (line 516)
- health:check failure prevents `executeTasks` from running (line 540)

Domain tests in `workflow-run-domain.test.ts` cover check repair exhaustion, `canRetryStage`, stale evidence, and retry after approval rejection.

### Security

**PASS.**

- No injection risks: commands run through `execFileAsync` with `shell: true` which is necessary for compound commands like `npm run build && npm test`. The `worktreePath` is resolved from project configuration, not user input.
- No secrets exposed in log output. `truncateLog` bounds output to 50KB and 5KB for excerpts.
- API approval guard validates all evidence fields before accepting, preventing bypass through malformed payloads.

### Spec Compliance

| # | Acceptance Criterion | Status | Evidence |
|---|---|---|---|
| 1 | Default Check execution runs full verification gate before AI review | **PASS** | `check-stage-runner.ts:62-74` registers `HealthGateCheck` in `getPreTaskChecks()`, which executes before `executeTasks()`. |
| 2 | Full verification failure blocks AI review, merge-ready, user approval | **PASS** | Pre-task check failure stops execution before tasks and post-task checks. Tests: `check-verification-regression.test.ts:203, 217`. |
| 3 | Passing result persisted as `health:check` | **PASS** | `DEFAULT_STAGE_DEFINITIONS` includes `health:check` before `review-passed` and `merge-ready` (`domain/index.ts:486`). Projection includes it (`workflow-run-projection.ts:140`). |
| 4 | Persisted evidence includes command, status, duration, summary; failure includes log excerpt | **PASS** | `health-gate-check.ts:136-149` (pass) and `165-194` (fail) include all fields. Test: `check-verification-regression.test.ts:434-527`. |
| 5 | Verification evidence bound to candidate; stale evidence invalidated | **PASS** | `candidateHeadSha` collected in `health-gate-check.ts:100`. Rebase invalidates `health:check` in `domain/index.ts:655`. `markStaleEvidence` at `domain/index.ts:405`. |
| 6 | Check approval only after full verification, review-passed, merge-ready all pass | **PASS** | `base-stage-runner.ts:438-446` and `529-536` both require passing `health:check`. Domain model `domain/index.ts:492-494` requires it too. |
| 7 | Missing/failed verification blocks approval and shows reason | **PASS** | `prepareApproval` returns descriptive messages. API approve endpoint at `issues.ts:2208-2230` rejects with clear error. |
| 8 | `mo issue show` and Web UI expose failed Check health gate | **PASS** | CLI: `issue.ts:478` allows `health:check` through the filter (`check.name !== 'health:check'`). Web UI: `PipelineView.tsx:808-818` shows failed verification panel. |
| 9 | `checks.buildTest` config remains usable | **PASS** | `check-stage-runner.ts:64-67` uses `loadHealthGatePolicies` which maps `checks.buildTest`. Tests: `check-verification-regression.test.ts:362-430`. |
| 10 | Regression: verification failure prevents approval | **PASS** | `check-verification-regression.test.ts:180-198`. |
| 11 | Regression: passing evidence precedes review, merge-ready, approval | **PASS** | `check-verification-regression.test.ts:230-241`. |

---

### Warnings (non-blocking)

1. **W1: Disabled-check inconsistency between runner and domain.** `base-stage-runner.ts:448-449` rejects disabled checks, but `domain/index.ts:492-494` (`WorkflowRun.buildApprovalOutput`) only checks `status !== 'passed'`. A disabled check returns `status: 'pass'` — so the domain path would accept it. In practice this is safe because the runner is the only production caller, but the domain model should ideally mirror the policy. Consider adding an `enabled` field check in `VerificationEvidence` or the domain's `buildApprovalOutput`.

2. **W2: Large diff includes drift/prerequisite features not in #225 scope.** The `computeDriftStateForIssue`, `buildDriftResponse`, `IssuePrerequisiteService`, and several new API endpoints (`/prerequisites`, `/check/retry-checkpoint`, `/check/rerun-review`, `/check/repair-review-findings`) are in the branch diff but are not part of the #225 issue scope. These make the diff harder to review in isolation and introduce risk of unrelated regressions. While they support the stale evidence detection user story, they should ideally be separate PRs.

3. **W3: `getCheckFailurePolicies()` returns empty.** The domain model defines `health:check` with `maxAttempts: 1` in `checkFailurePolicies`, but the runner overrides `getCheckFailurePolicies()` to return `[]`. This means the auto-fix path for `health:check` is dead code in the domain definition. Either remove the domain-level policy entry or wire it through the runner.

4. **W4: `checkedAt` uses `new Date().toISOString()` rather than the actual check execution timestamp.** In `base-stage-runner.ts:550` and `domain/index.ts:982`, `checkedAt` is set to the current time when building approval output, not when the check actually ran. For long-running checks, this could be minutes off. Consider propagating the actual check start time from `HealthGateCheck.run()`.

5. **W5: `extractVerificationEvidence` uses `healthCheck.status === 'passed'` for mapping but `StageRun` stores status as `'passed'` while `CheckResult` uses `'pass'`.** The mapping at `domain/index.ts:987` handles this (`'passed' ? 'pass'`), but the `buildApprovalOutput` in `base-stage-runner.ts:529-536` checks against `'pass'` (CheckResult status). These are different status types used in different layers — correct but fragile.

---

### Summary

The implementation is correct and comprehensive. The core invariant — Check approval requires passing full verification evidence for the same candidate implementation — is enforced at three layers: runner orchestration (pre-task checks), approval preparation (runner guard), and approve API (server-side validation). Test coverage is strong with focused regression tests.

The warnings are non-blocking: a disabled-check edge case in the domain model, scope creep in the diff, a dead auto-fix policy entry, imprecise timestamps, and status type mismatches between layers. None of these affect correctness in production paths.

<promise>PASS</promise>
