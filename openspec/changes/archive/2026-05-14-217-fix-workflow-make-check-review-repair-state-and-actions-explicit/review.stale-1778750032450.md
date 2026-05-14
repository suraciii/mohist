## Findings

1. Error: `POST /api/issues/:number/check/repair-review-findings` does not actually start a repair for failed Check runs. The API appends a `fix-review-findings` task via `scheduleFixReviewFindings`, but `doScheduleFixReviewFindings()` never transitions a failed `WorkflowRun` back to `running`; it only appends the task and saves the still-failed run (`packages/cli/src/services/workflow-application-service.ts:139-171`). `WorkflowRun.nextWork()` returns `{ kind: 'failed' }` whenever `run.status === 'failed'` (`packages/cli/src/workflow/domain/index.ts:836-839`), so the new task is not executable. The route also never enqueues `resume-pipeline` or resets the blocked issue state, unlike `retry-checkpoint` and `rerun-review` (`packages/cli/src/api/issues.ts:3393-3466`, `3468-3558`, `3560-3625`). This violates the explicit repair-action requirement because clicking `Fix review findings` can return `202` without any repair attempt starting. Suggested fix: when scheduling repair from a failed Check review, either call a workflow transition that reopens the failed Check stage before appending/reusing the repair task, or add a dedicated application-service method that both reactivates the issue/workflow and enqueues `resume-pipeline`; cover this with API tests for `POST /check/repair-review-findings` on failed and already-running repair states.

## Spec Compliance

### http-api/spec.md

- PASS `check-review-repair-state` / Failed review exposes repair state: `StageStateService` computes and attaches `checkRepair` with attempts and availability (`packages/cli/src/services/stage-state-service.ts:472-540`, `754-757`); backend coverage exists (`packages/cli/tests/stage-state-service.test.ts:506-527`).
- PASS `check-review-repair-state` / Repair completion remains separate from review verdict: projection keeps `lastRepairStatus` from the task and `followUpReviewStatus` from `review-passed` (`packages/cli/src/services/stage-state-service.ts:506-537`); test covers completed repair plus failed follow-up review (`packages/cli/tests/stage-state-service.test.ts:529-560`).
- PASS `check-review-repair-state` / Exhaustion is explicit: exhausted state sets `attemptsRemaining = 0`, `repairAvailable = false`, `stopReason = max-repair-attempts-reached` (`packages/cli/src/services/stage-state-service.ts:490-519`); test coverage at `packages/cli/tests/stage-state-service.test.ts:549-559`.
- PASS `check-review-recovery-actions` / Retry checkpoint does not schedule exhausted repair: dedicated endpoint returns checkpoint-retry wording and preserves exhaustion semantics (`packages/cli/src/api/issues.ts:3414-3439`); domain retry test proves no new repair task is appended (`packages/cli/tests/workflow-run-domain.test.ts:444-486`); API test covers exhausted checkpoint retry (`packages/cli/tests/api-routes.test.ts:1176-1261`).
- PASS `check-review-recovery-actions` / Rerun review only is distinct from repair: dedicated endpoint clears review artifacts/checkpoints and returns `rerunning review only (no repair task will be added)` (`packages/cli/src/api/issues.ts:3468-3554`).
- FAIL `check-review-recovery-actions` / Fix review findings is explicit and bounded: the dedicated endpoint exists and exhaustion messaging is explicit (`packages/cli/src/api/issues.ts:3560-3615`), but the scheduled repair is not runnable for failed Check runs because the workflow remains failed and the route never resumes execution (`packages/cli/src/services/workflow-application-service.ts:139-171`, `packages/cli/src/workflow/domain/index.ts:836-839`, `packages/cli/src/api/issues.ts:3560-3625`).

### web-ui/spec.md

- PASS `check-review-repair-surface` / Check repair state is visible: Issue Detail reads `checkRepair` (`packages/cli/web/src/components/IssueDetailPage.tsx:107-109`) and `PipelineView` renders auto-fix status, attempts, last repair, follow-up review, stop reason, and unresolved findings (`packages/cli/web/src/components/PipelineView.tsx:1117-1197`).
- PASS `check-review-repair-surface` / Completed repair followed by failed review is not contradictory: UI shows `follow-up review failed` alongside completed repair state (`packages/cli/web/src/components/PipelineView.tsx:1157-1173`); component tests cover this (`packages/cli/web/src/components/check-repair-display.test.tsx:183-252`).
- PASS `check-review-repair-surface` / Repair exhaustion explains next action: exhaustion guidance is rendered (`packages/cli/web/src/components/PipelineView.tsx:1191-1196`); covered by tests (`packages/cli/web/src/components/check-repair-display.test.tsx:255-309`).
- PASS `check-review-repair-surface` / Recovery actions use explicit intent labels: Issue Detail renders `Fix review findings`, `Retry checkpoint`, and `Rerun review only` when `checkRepair` exists (`packages/cli/web/src/components/IssueDetailPage.tsx:683-707`); tests verify labels and absence of ambiguous `Retry` (`packages/cli/web/src/components/check-repair-display.test.tsx:312-343`).
- PASS `check-review-repair-regressions` / Backend repair projection is covered: `packages/cli/tests/stage-state-service.test.ts:506-629`.
- PASS `check-review-repair-regressions` / Exhausted retry does not look like repair: `packages/cli/tests/workflow-run-domain.test.ts:444-486` and `packages/cli/tests/api-routes.test.ts:1176-1261`.
- PASS `check-review-repair-regressions` / Frontend display semantics are covered: `packages/cli/web/src/components/check-repair-display.test.tsx:183-343`.

### workflow-run/spec.md

- PASS `check-review-repair-policy` / Failed review schedules repair within budget: WorkflowRun stage definition is authoritative with `maxAttempts: 1` and `fix-review-findings` (`packages/cli/src/workflow/domain/index.ts:453-466`); failed review auto-schedules repair only while `scheduledFixCount < policy.maxAttempts` (`packages/cli/src/workflow/domain/index.ts:682-689`); domain coverage at `packages/cli/tests/workflow-run-domain.test.ts:221-248`.
- PASS `check-review-repair-policy` / Failed review stops when budget is exhausted: no additional repair is appended once the budget is used (`packages/cli/src/workflow/domain/index.ts:682-689` with `scheduledFixCount` from `355-357`); projection shows exhaustion (`packages/cli/src/services/stage-state-service.ts:490-519`); domain coverage at `packages/cli/tests/workflow-run-domain.test.ts:444-486`.
- PASS `check-review-repair-policy` / Retry does not imply another repair: `retryStage()` resets review work without appending another fix task after an exhausted repair (`packages/cli/src/workflow/domain/index.ts:740-805`); covered by `packages/cli/tests/workflow-run-domain.test.ts:444-486`.

## Quality Notes

- Correctness: one error-level issue in the failed-run repair action path.
- Complexity: the new helpers and UI panel are small and locally scoped; no obvious function-length regression stood out in reviewed paths.
- Test coverage: targeted tests passed with `npx vitest run tests/stage-state-service.test.ts tests/workflow-run-domain.test.ts tests/api-routes.test.ts` and `npx vitest run web/src/components/check-repair-display.test.tsx`, but there is no direct API regression test for `POST /api/issues/:number/check/repair-review-findings`.
- Security: no new obvious injection or secret-handling issues found in reviewed changes.

<promise>FAIL</promise>
