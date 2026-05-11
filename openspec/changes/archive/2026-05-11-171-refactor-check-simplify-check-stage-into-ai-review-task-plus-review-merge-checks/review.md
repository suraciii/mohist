# Review Report

## Result: PASS

All 8 acceptance criteria pass. All 4 spec sections pass (with minor warnings). 2025 tests pass (6 skipped). 6 warnings documented (W1–W6), none blocking.

## Dimensions

### Correctness: PASS

No blocking logic errors or bugs found. The `CheckStageRunner` correctly implements the simplified model: `ai-review` as a single task, followed by `review-passed`, `merge-ready`, and `user-approval` checks. The `ReviewPassedCheck` correctly parses `review.md` verdict and returns `error` status (not `fail`) when the artifact is missing, which prevents it from being treated as a user-facing check decision point. The `MergeReadyCheck` correctly verifies mergeability. The `beforeRecheckAfterFix` correctly invalidates review artifacts and checkpoints for both `review-passed` and `merge-ready` repair flows. The `handleApprovalCheck()` in `base-stage-runner.ts` correctly gates approval on `review-passed` PASS verdict and `merge-ready` pass status. The API approval endpoint at `issues.ts:1134-1194` validates snapshot SHA, worktree HEAD, and worktree cleanliness.

**Warning W1 (Low):** `merge-ready` always invalidates review even when HEAD didn't change. This is conservative (safe but wasteful). The `beforeRecheckAfterFix` at `check-stage-runner.ts:256-272` unconditionally deletes `review.md` and `ai-review` checkpoint. Design spec D4 says "any merge-readiness work that changes HEAD must reset review truth," but the implementation resets even on no-op rebase. `runMergeRepairTask` captures `headChanged` in output but nothing reads it.

### Complexity: PASS

Most functions are under 50 lines. `CheckStageRunner.executeTasks()` at ~250 lines is complex due to agent session lifecycle management (retry, checkpoint, event emission), but the complexity is inherent to the domain. Cyclomatic complexity is acceptable — the main execution path is linear with early-exit error branches. `beforeRecheckAfterFix` uses `require('fs')` for synchronous file deletion (lines 242, 261), which is a minor style issue but acceptable in a CLI/server context.

### Test Coverage: PASS

`simplified-check-stage-regression.test.ts` (20 tests) directly verifies each acceptance criterion. `check-stage-re-review-convergence.test.ts` (14 tests) covers repair/re-review/approval cycles. `check-stage-ordering.test.ts` (17 tests) verifies check ordering and no pre-task checks. `boundary-regression.test.ts` (26 tests) covers task/check boundaries. `stage-exit-health-gate-regression.test.ts` (24 tests) verifies health gate mechanics. Total: 2025 tests pass.

**Gap G1:** The `merge-ready` repair → review invalidation → re-review → approval full cycle is not tested end-to-end.

**Gap G2:** `runMergeRepairTask` records `headChanged` but `beforeRecheckAfterFix` doesn't condition on it (see W1).

**Gap G3:** `api-routes.test.ts` line 411 stores check results under `ai-review` name, not `review-passed`. Functional due to fallback at `issues.ts:108-109`, but inconsistent with the new model.

### Security: PASS

No injection risks or exposed secrets. `require('fs')` operations in `beforeRecheckAfterFix` use hardcoded paths derived from internal state, not user input. SQL queries use parameterized statements via `DatabaseManager`. No user-controlled strings enter path construction without validation.

### Spec Compliance: PASS

#### http-api/spec.md: PASS (with Warning W2)

- `GET /api/issues/:number` exposes `CheckSuiteChecks` with `review-passed`, `merge-ready`, `user-approval` keys. ✅ (`check-suite-repo.ts:27-33`, `types/index.ts:146-150`)
- Check suite endpoint uses simplified keys. ✅ (`web/src/lib/types.ts:627-630`, `useCheckSuiteProgress.ts:8-12`)
- Approval validates `review-passed` PASS verdict and snapshot SHA. ✅ (`issues.ts:1134-1194`, `base-stage-runner.ts:297-316`)
- **W2:** `issues.ts:108-109` and `issues.ts:1170` fall back to `ai-review` check name for legacy compatibility. Matches design D6 ("read paths may tolerate old keys") but not documented in spec.

#### pipeline-model/spec.md: PASS

- `ai-review` is a task, not a check. ✅ (`check-stage-runner.ts:292-293`, `stage-state-service.ts:299`)
- Visible checks: `review-passed`, `merge-ready`, `user-approval`. ✅ (`check-stage-runner.ts:35-39`)
- Internal evidence stays internal. ✅ (`health:check` and `integration-health-gate-preview` modules exist but are not used by `CheckStageRunner`)
- Missing/unparsable artifact → `ai-review` task failure. ✅ (`check-stage-runner.ts:408-471`)
- `review-passed` reads verdict from final review. ✅ (`review-passed-check.ts:19-54`)
- Repair creates actual task, not empty fix. ✅ (`check-stage-runner.ts:50-53`, `review-fix-task.ts`)
- Merge repair changes invalidate review. ✅ (`check-stage-runner.ts:256-272`)

#### web-ui/spec.md: PASS (with Warning W3)

- UI shows `ai-review` as task history. ✅ (`stage-state-service.ts:299` registers it as a task)
- UI shows `review-passed` and `merge-ready` as checks. ✅ (`useCheckSuiteProgress.ts:8-12`)
- **W3:** `PipelineView.tsx:878-880` still references `merge-readiness` and `integration-health-gate-preview` in `DoneEvidencePanel` for historical integration evidence display. These are supporting details, not primary Check-stage checks, so technically compliant with spec.

#### workflow-engine/spec.md: PASS

- `ai-review` task artifact contract: missing → fail, unparsable → fail. ✅ (`check-stage-runner.ts:408-471`, `utils.ts:86-96`)
- Valid review enables `review-passed`. ✅ (`review-passed-check.ts:19-54`)
- `review-passed` failure creates dynamic repair based on findings. ✅ (`check-stage-runner.ts:65-81`)
- Repair invalidates old review and re-runs `ai-review`. ✅ (`check-stage-runner.ts:238-253`)
- Re-review remains approval truth. ✅ (`base-stage-runner.ts:297-358`, `base-stage-runner.ts:462-491`)
- `merge-ready` invalidates review on code change. ✅ (`check-stage-runner.ts:256-272`)
- Approval based on current snapshot. ✅ (`base-stage-runner.ts:330-346`, `issues.ts:1134-1194`)

## Acceptance Criteria Verification

### AC-1: Check stage initial user-visible task is `ai-review`

**PASS.** `check-stage-runner.ts:292-293` defines `type: 'ai-review'`. `stage-state-service.ts:299` registers `{ taskId: 'ai-review', title: 'AI review' }`. No other tasks. Verified: `check-stage-ordering.test.ts`.

### AC-2: Check stage user-visible checks are `review-passed`, `merge-ready`, and approval

**PASS.** `check-stage-runner.ts:35-39` sets `[ReviewPassedCheck(), MergeReadyCheck(), UserApprovalCheck(Stage.Check)]`. `types/index.ts:146-150`, `web/src/lib/types.ts:627-630`, `check-suite-repo.ts:27-33`, `useCheckSuiteProgress.ts:8-12` all align. Verified: `simplified-check-stage-regression.test.ts`.

### AC-3: Missing/malformed review artifact → `ai-review` task failure

**PASS.** `check-stage-runner.ts:408-471` throws on missing/invalid artifact, surfacing as task failure. `review-passed-check.ts:22-27` returns `error` (not `fail`) with message pointing to `ai-review` task. Verified: `simplified-check-stage-regression.test.ts`.

### AC-4: `ai-review` auto-fixes simple issues and regenerates final review

**PASS.** `check-stage-runner.ts:397-515` runs agent session with retry on missing artifact. `review-fix-task.ts:45-168` runs repair agent with review findings. `check-stage-runner.ts:238-253` invalidates stale review before re-check. Verified: `simplified-check-stage-regression.test.ts`, `check-stage-re-review-convergence.test.ts`.

### AC-5: `review-passed` failure creates dynamic repair task

**PASS.** `check-stage-runner.ts:50-53` defines policy `{ checkName: 'review-passed', fixTaskId: 'repair-review-findings', maxAttempts: 3 }`. `runFixTask()` at lines 65-81 dispatches `runReviewFixTask` based on actual findings. No predeclared empty fix task. Verified: `simplified-check-stage-regression.test.ts`, `check-stage-re-review-convergence.test.ts`.

### AC-6: `merge-ready` code change invalidates review and re-runs `ai-review`

**PASS.** `check-stage-runner.ts:256-272` deletes `review.md` and `ai-review` checkpoint on `merge-ready` repair. See W1 for nuance (always invalidates, even when HEAD unchanged).

### AC-7: User approval based on current snapshot's review result

**PASS.** `base-stage-runner.ts:297-316` requires `review-passed` PASS verdict and `merge-ready` pass. `issues.ts:1134-1194` validates snapshot SHA, worktree HEAD, and cleanliness. Verified: `simplified-check-stage-regression.test.ts`.

### AC-8: UI does not require understanding `health:check`, `integration-health-gate-preview`, etc.

**PASS.** Primary Check-stage UI (`useCheckSuiteProgress.ts`, `types.ts`) uses only `review-passed`, `merge-ready`, `user-approval`. CLI (`issue.ts:342-344`) filters old names. See W3 for residual references in evidence panel.

## Warnings

### W1: `merge-ready` always invalidates review regardless of HEAD change
`beforeRecheckAfterFix()` at `check-stage-runner.ts:256-272` unconditionally deletes `review.md` and `ai-review` checkpoint. Design D4 says "any merge-readiness work that changes HEAD must reset review truth," but implementation resets even on no-op rebase. `runMergeRepairTask` captures `headChanged` in output but nothing reads it. **Severity:** Low — over-invalidating is safer than under-invalidating.

### W2: Legacy `ai-review` check name fallback in API
`issues.ts:108-109` and `issues.ts:1170` fall back to `ai-review` check name. Aligns with design D6 ("read paths may tolerate old keys") but not documented in spec. **Severity:** Low — functional backward compatibility.

### W3: `PipelineView.tsx` references old check names in evidence panel
`PipelineView.tsx:878-880` references `merge-readiness` and `integration-health-gate-preview` in `DoneEvidencePanel`. These are supporting details (historical integration evidence), not primary Check-stage checks. **Severity:** Low.

### W4: Old check classes still exist
`AiReviewCheck`, `MergeReadinessCheck`, `IntegrationHealthGatePreviewCheck` still exist but are not used by `CheckStageRunner`. Exported from `workflow/index.ts`. **Severity:** Low — dead code for Check stage, may be used by other stages or tests.

### W5: `stage-state-service.ts:299` has stale `build-test` static task def
`CHECK_TASK_DEFS` includes `build-test` alongside `ai-review` and `user-approval`. The simplified model has `ai-review` as the only task. Only used for stage state seeding, doesn't affect runner behavior. **Severity:** Low.

### W6: CLI uses blocklist instead of allowlist for check filtering
`issue.ts:342-344` filters `ai-review`, `merge-readiness`, `integration-health-gate-preview` from display. An allowlist (`review-passed`, `merge-ready`, `user-approval`) would be more robust. **Severity:** Low.

<promise>PASS</promise>