# Review Report

## Result: FAIL

## Findings

### 1. Stale `ai-review` FAIL is still exposed as current check truth in CLI execution display

- Severity: FAIL
- Evidence:
  - `packages/cli/src/workflow/base-stage-runner.ts:208-238` appends the re-review PASS to `continuedResults` and keeps the earlier failed `ai-review` entry in the same `checkResults` array.
  - `packages/cli/src/api/issues.ts:386-387` returns raw stage executions from `stageExecutionRepo.findByIssueId(issue.id)`.
  - `packages/cli/src/cli/commands/issue.ts:261-305` fetches `/issues/:number/executions` and prints every `checkResult` from the latest execution without deduplicating `ai-review`.
- Impact:
  - `mo issue show` can still display the old `ai-review failed` result alongside the new PASS approval output for the same check cycle.
  - This violates the requirement that the latest authoritative re-review replace stale AI review truth before approval.
- Fix suggestion:
  - `packages/cli/src/workflow/base-stage-runner.ts:208-238`: replace the current `ai-review` result on successful re-review instead of appending a second authoritative-looking entry.
  - `packages/cli/src/cli/commands/issue.ts:274-305`: if historical attempts must remain persisted, render only the latest `ai-review` entry for current truth.

### 2. Check approval API still allows approval without authoritative PASS when `CheckSuite` is absent

- Severity: FAIL
- Evidence:
  - `packages/cli/src/api/issues.ts:1078-1143` validates `ai-review` verdict only inside the `if (checkSuiteRepo) { const activeSuite = ... }` branch.
  - When there is no active `CheckSuite`, the endpoint still calls `issueRepo.setApprovalState(... status: 'approved' ...)` at `packages/cli/src/api/issues.ts:1132-1137` and advances to `Stage.Integrate` at `packages/cli/src/api/issues.ts:1140`.
  - `packages/cli/tests/api-routes.test.ts:341-367` expects a successful Check-stage approval with `approvalState.output = { test: true }`, demonstrating the missing PASS/snapshot guard.
- Impact:
  - A stale or manually-written awaiting approval state can advance to Integrate without a validated PASS review snapshot.
  - This violates the approval guard requirements for rejecting non-PASS review truth and snapshot drift.
- Fix suggestion:
  - `packages/cli/src/api/issues.ts:1078-1143`: require an authoritative `ai-review` PASS even when `CheckSuite` is absent by reading the latest persisted Check-stage execution result and validating `approvalState.output.snapshotSha` against HEAD and worktree cleanliness.
  - `packages/cli/tests/api-routes.test.ts:341-367`: change the test to expect rejection until authoritative PASS plus snapshot convergence exist.

### 3. Required authoritative AI review metadata is not persisted on the main execution path

- Severity: FAIL
- Evidence:
  - `packages/cli/src/workflow/checks/ai-review-check.ts:45-53` returns only `verdict`, `reviewReport`, and `fixSuggestions`.
  - `packages/cli/src/workflow/base-stage-runner.ts:127-132` persists raw `checkResults` directly after check execution.
  - `packages/cli/src/workflow/base-stage-runner.ts:365-396` defines `persistAuthoritativeAiReview(...)`, but there is no call site using it in the normal Check-stage execution/recheck flow.
  - `packages/cli/src/workflow/base-stage-runner.ts:419-447` adds only `snapshotSha` and `convergedAt` during approval convergence; it still does not populate `reviewArtifactPath` or `selfCheckArtifactPath`.
- Impact:
  - The persisted authoritative `ai-review` result is not fully snapshot-bound as required.
  - Downstream readers cannot rely on one persisted object containing verdict, reviewed snapshot, and artifact paths.
- Fix suggestion:
  - `packages/cli/src/workflow/base-stage-runner.ts:365-396`: invoke `persistAuthoritativeAiReview()` immediately after each authoritative `ai-review` result is established.
  - `packages/cli/src/workflow/check-stage-runner.ts:129-157` and `packages/cli/src/workflow/base-stage-runner.ts:203-238`: pass the current HEAD SHA plus `review.md` and `review-self-check.md` artifact paths into that persistence step.

## Dimensions

### Correctness: FAIL

- The implementation still exposes contradictory current truth in CLI output and leaves an approval path that can bypass authoritative PASS validation.

### Complexity: PASS

- No new function in the reviewed change obviously exceeds the requested threshold by itself due to this issue's edits.
- `packages/cli/src/workflow/check-stage-runner.ts:159-403` and `packages/cli/src/workflow/base-stage-runner.ts:112-245` remain large and branch-heavy, but this is a maintainability warning rather than a new correctness failure.

### Test Coverage: FAIL

- Targeted tests pass: `npm test -- --run tests/workflow/check-stage-re-review-convergence.test.ts tests/api-routes.test.ts`.
- Coverage is missing for the failing current-truth CLI path and incorrectly encodes the approval bypass as expected behavior in `packages/cli/tests/api-routes.test.ts:341-367`.

### Security: PASS

- No injection, secret exposure, or unsafe input handling issue was identified in the reviewed implementation.

### Spec Compliance: FAIL

- Multiple acceptance criteria are not fully met, detailed below with concrete evidence.

## Spec Compliance

### Acceptance Criterion 1

- Criterion: auto-fix 后 re-review PASS 时，issue show/API 显示的是新的 ai-review PASS，而不是旧的 FAIL。
- Verdict: FAIL
- Evidence:
  - `packages/cli/src/workflow/base-stage-runner.ts:208-238` keeps both old and new `ai-review` entries.
  - `packages/cli/src/cli/commands/issue.ts:261-305` prints all execution check results.
  - `packages/cli/src/api/issues.ts:386-387` returns raw execution history used by the CLI.

### Acceptance Criterion 2

- Criterion: re-review 必须基于 fix 后的当前代码快照重新计算 verdict；旧 review artifact 不得直接作为当前 recheck truth 复用。
- Verdict: PASS
- Evidence:
  - `packages/cli/src/workflow/check-stage-runner.ts:129-157` clears `review` and `review-self-check` checkpoint steps, deletes `review.md` and `review-self-check.md`, and reruns task generation.
  - `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts:254-362` verifies re-review generates a fresh report instead of reusing the stale one.

### Acceptance Criterion 3

- Criterion: authoritative ai-review result 必须绑定到当前待审批代码快照。
- Verdict: FAIL
- Evidence:
  - `packages/cli/src/workflow/checks/ai-review-check.ts:45-53` does not include `snapshotSha`.
  - `packages/cli/src/workflow/base-stage-runner.ts:419-447` adds `snapshotSha` only during approval request, not when the authoritative result is first persisted.

### Acceptance Criterion 4

- Criterion: approval output 使用最新 re-review report，且该 report 与当前代码快照一致。
- Verdict: PASS
- Evidence:
  - `packages/cli/src/workflow/base-stage-runner.ts:274-332` selects the latest `ai-review` result and writes `reviewReport` and convergence `snapshotSha` into approval output.
  - `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts:365-474` verifies latest PASS report and matching converged snapshot SHA.

### Acceptance Criterion 5

- Criterion: auto-fix 后若有未提交修改，系统必须在进入 approval 前处理：自动提交或阻塞并提示。
- Verdict: PASS
- Evidence:
  - `packages/cli/src/workflow/base-stage-runner.ts:314-321` blocks approval when convergence commit creation fails.
  - `packages/cli/src/git/worktree-manager.ts:906-955` auto-commits pending changes when possible and returns a clear error when the worktree remains dirty or commit fails.
  - `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts:477-601` covers commit-failure blocking behavior.

### Acceptance Criterion 6

- Criterion: review.md、review-self-check.md、最终 ai-review verdict 和相关修复代码处于一致状态。
- Verdict: FAIL
- Evidence:
  - `packages/cli/src/workflow/base-stage-runner.ts:208-238` retains both stale FAIL and latest PASS in current `checkResults`, so the final surfaced verdict can disagree with regenerated review artifacts.
  - `packages/cli/src/cli/commands/issue.ts:261-305` can show stale FAIL while approval output reflects PASS.

### Acceptance Criterion 7

- Criterion: 同一个 check cycle 中，不允许同时存在“当前 ai-review FAIL”和“当前 approval output PASS”这类互相矛盾的最终状态。
- Verdict: FAIL
- Evidence:
  - `packages/cli/src/workflow/base-stage-runner.ts:208-238` persists both FAIL and PASS entries in the same Check-stage execution.
  - `packages/cli/src/workflow/base-stage-runner.ts:327-332` writes PASS approval output from the latest result, creating a contradictory surfaced state when the stale FAIL remains visible.

### Acceptance Criterion 8

- Criterion: 测试覆盖：ai-review FAIL -> auto-fix -> re-review PASS -> persisted PASS -> approval requested。
- Verdict: PASS
- Evidence:
  - `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts:159-251` covers FAIL -> fix -> re-review PASS -> approval requested.

### Acceptance Criterion 9

- Criterion: 测试覆盖：fix-review-findings 修改代码后，旧 review.md 不会被直接复用于 recheck。
- Verdict: PASS
- Evidence:
  - `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts:254-362` covers the stale artifact non-reuse path.

### Acceptance Criterion 10

- Criterion: 测试覆盖：auto-fix 产生未提交修改但提交失败时，不允许进入普通 approval。
- Verdict: PASS
- Evidence:
  - `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts:477-601` covers both dirty-after-commit and git commit failure blocking approval.

### Acceptance Criterion 11

- Criterion: 测试覆盖：re-review FAIL 时不会误进入 PASS/approval，并保留最新 FAIL truth。
- Verdict: PASS
- Evidence:
  - `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts:604-761` verifies no approval is requested and the latest FAIL report is preserved.

## Changed Files Reviewed

- `packages/cli/src/workflow/base-stage-runner.ts`
- `packages/cli/src/workflow/check-stage-runner.ts`
- `packages/cli/src/workflow/checks/ai-review-check.ts`
- `packages/cli/src/api/issues.ts`
- `packages/cli/src/cli/commands/issue.ts`
- `packages/cli/src/git/worktree-manager.ts`
- `packages/cli/tests/workflow/check-stage-re-review-convergence.test.ts`
- `packages/cli/tests/api-routes.test.ts`

<promise>FAIL</promise>
