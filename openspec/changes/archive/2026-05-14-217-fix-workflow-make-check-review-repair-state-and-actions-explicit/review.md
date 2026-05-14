## Findings

No error-level findings.

Warning:

- `packages/cli/web/src/components/IssueDetailPage.tsx:111` prefers a frontend-recomputed `workflowRunCheckRepair` over the backend-projected `stageState.checkRepair`, and `packages/cli/web/src/lib/workflow-run-utils.ts:43-84` reimplements `checkRepair` with `const maxAttempts = 1`. This passes current specs because backend and UI both currently assume `1`, but it reintroduces the duplicated policy/design risk this change set was meant to remove. Suggested change: stop recomputing `checkRepair` in `workflowRunToStageStateMap`, or have workflow-run responses carry the authoritative projection from `StageStateService` so the UI consumes one fact source.

## Correctness

PASS. The backend now projects structured Check repair state from authoritative workflow policy and task/check history, keeps repair task completion separate from review verdict, and exposes distinct retry/rerun/repair actions.

Evidence:

- Authoritative `review-passed` repair policy is defined in `packages/cli/src/workflow/domain/index.ts:459-472` with `fix-review-findings` and `maxAttempts: 1`.
- `CheckStageRunner` no longer exposes its own competing repair budget via `getCheckFailurePolicies()` and returns `[]` in `packages/cli/src/workflow/check-stage-runner.ts:52-54`.
- `StageStateService` computes `checkRepair` from task/check state and `getCheckFailurePolicy(...)` in `packages/cli/src/services/stage-state-service.ts:475-543`.
- Exhausted retry avoids creating another repair task in `packages/cli/src/workflow/domain/index.ts:746-810` and the dedicated retry-checkpoint API messaging is explicit in `packages/cli/src/api/issues.ts:3412-3459`.
- Explicit repair scheduling/idempotency lives in `packages/cli/src/services/workflow-application-service.ts:141-186` and `packages/cli/src/api/issues.ts:3558-3645`.

## Complexity

PASS with warning. New helpers are reasonably contained, but the frontend duplicates non-trivial `checkRepair` derivation logic in `packages/cli/web/src/lib/workflow-run-utils.ts:43-84`, increasing maintenance complexity and drift risk.

## Test Coverage

PASS.

Executed:

- `npx vitest run tests/stage-state-service.test.ts tests/api-routes.test.ts tests/workflow-run-domain.test.ts`
- `npx vitest run src/components/check-repair-display.test.tsx src/components/IssueDetailPage.test.tsx`
- `npm run build`

Results:

- Backend focused tests: 149 passed.
- Frontend focused tests: 33 passed.
- Build passed.

Note: initial `npm test -- --runInBand ...` commands failed because this Vitest version does not support `--runInBand`; rerunning with `npx vitest run ...` succeeded.

## Security

PASS. No new obvious injection or secret-handling issues found in the reviewed paths. The added endpoints operate on existing issue/workflow identifiers and preserve stage checks before mutating workflow state.

## Spec Compliance

### http-api/spec.md

- PASS `check-review-repair-state` / failed review exposes repair state.
  Evidence: `packages/cli/src/services/stage-state-service.ts:758-760` attaches `checkRepair`; fields are populated in `packages/cli/src/services/stage-state-service.ts:530-543`. Tests assert attempts and availability in `packages/cli/tests/stage-state-service.test.ts:506-527`.
- PASS `check-review-repair-state` / repair completion remains separate from review verdict.
  Evidence: `lastRepairStatus` and `followUpReviewStatus` are populated independently in `packages/cli/src/services/stage-state-service.ts:510-543`. Regression test covers completed repair plus failed follow-up review in `packages/cli/tests/stage-state-service.test.ts:529-560`.
- PASS `check-review-repair-state` / exhaustion is explicit.
  Evidence: `attemptsRemaining`, `repairAvailable`, and `stopReason` derive explicitly in `packages/cli/src/services/stage-state-service.ts:493-523`. Test coverage in `packages/cli/tests/stage-state-service.test.ts:548-559`.
- PASS `check-review-recovery-actions` / retry checkpoint does not schedule exhausted repair.
  Evidence: exhausted retry path returns checkpoint wording only in `packages/cli/src/api/issues.ts:3414-3437`; workflow retry does not append another fix task in `packages/cli/src/workflow/domain/index.ts:793-810`. Tests: `packages/cli/tests/workflow-run-domain.test.ts:444-486`, `packages/cli/tests/api-routes.test.ts:1241-1258`.
- PASS `check-review-recovery-actions` / rerun review only is distinct from repair.
  Evidence: dedicated endpoint `/:number/check/rerun-review` clears review artifacts/checkpoints and says no repair task will be added in `packages/cli/src/api/issues.ts:3466-3552`. Tests: `packages/cli/tests/api-routes.test.ts:1265-1325`.
- PASS `check-review-recovery-actions` / fix review findings is explicit and bounded.
  Evidence: dedicated endpoint `/:number/check/repair-review-findings` in `packages/cli/src/api/issues.ts:3558-3645`; scheduling/idempotency/budget enforcement in `packages/cli/src/services/workflow-application-service.ts:148-185`. Tests: `packages/cli/tests/api-routes.test.ts:1331-1572`.

### web-ui/spec.md

- PASS `check-review-repair-surface` / repair state is visible.
  Evidence: `CheckRepairPanel` renders status, attempts, last repair, follow-up review, stop reason, and unresolved summary in `packages/cli/web/src/components/PipelineView.tsx:1117-1197`; panel is shown for blocked Check issues in `packages/cli/web/src/components/PipelineView.tsx:1434-1435`.
- PASS `check-review-repair-surface` / completed repair followed by failed review is not contradictory.
  Evidence: UI renders `follow-up review failed` alongside completed repair in `packages/cli/web/src/components/PipelineView.tsx:1157-1173`. Tests: `packages/cli/web/src/components/check-repair-display.test.tsx:183-252`.
- PASS `check-review-repair-surface` / repair exhaustion explains next action.
  Evidence: exhaustion guidance is rendered in `packages/cli/web/src/components/PipelineView.tsx:1191-1196`. Tests: `packages/cli/web/src/components/check-repair-display.test.tsx:255-309`.
- PASS `check-review-repair-surface` / recovery actions use explicit intent labels.
  Evidence: Issue Detail shows `Fix review findings`, `Retry checkpoint`, and `Rerun review only` in `packages/cli/web/src/components/IssueDetailPage.tsx:688-712`. Tests: `packages/cli/web/src/components/check-repair-display.test.tsx:312-361` and `packages/cli/web/src/components/IssueDetailPage.test.tsx:410-412`.
- PASS `check-review-repair-regressions` backend/frontend coverage.
  Evidence: backend regression coverage in `packages/cli/tests/stage-state-service.test.ts:506-653`, `packages/cli/tests/workflow-run-domain.test.ts:444-517`, and `packages/cli/tests/api-routes.test.ts:1180-1572`; frontend regression coverage in `packages/cli/web/src/components/check-repair-display.test.tsx:183-361`.

### workflow-run/spec.md

- PASS `check-review-repair-policy` / WorkflowRun is authoritative.
  Evidence: canonical Check policy lives in `packages/cli/src/workflow/domain/index.ts:459-472`; runner policy override removed in `packages/cli/src/workflow/check-stage-runner.ts:52-54`; `StageStateService` reads policy via `getCheckFailurePolicy(...)` in `packages/cli/src/services/stage-state-service.ts:488-495`.
- PASS `check-review-repair-policy` / failed review schedules repair within budget.
  Evidence: `recordCheckResult` schedules `fix-review-findings` only when `scheduledFixCount < policy.maxAttempts` in `packages/cli/src/workflow/domain/index.ts:688-699`.
- PASS `check-review-repair-policy` / failed review stops when budget is exhausted.
  Evidence: when attempts are exhausted, `recordCheckResult` fails the stage instead of scheduling another fix in `packages/cli/src/workflow/domain/index.ts:688-708`.
- PASS `check-review-repair-policy` / retry does not imply another repair.
  Evidence: `retryStage` only invalidates `ai-review`/checks after completed repair and does not append tasks in `packages/cli/src/workflow/domain/index.ts:746-810`. Regression test: `packages/cli/tests/workflow-run-domain.test.ts:444-486`.

## Overall

PASS with warnings.

<promise>PASS</promise>
