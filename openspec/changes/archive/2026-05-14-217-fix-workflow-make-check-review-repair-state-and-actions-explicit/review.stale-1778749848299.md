## Findings

1. Error: `POST /api/issues/:number/check/retry-checkpoint` does not perform checkpoint retry behavior.
File: `packages/cli/src/api/issues.ts:3362-3400`
Evidence: unlike the existing `POST /:number/retry` flow at `packages/cli/src/api/issues.ts:3125-3200`, this handler only returns success JSON and never clears blocked state, never updates issue status, never calls `workflowApplicationService.retryStage(...)`, and never enqueues `resume-pipeline`. The spec requires retry checkpoint recovery to remain a real distinct action, not just wording. As implemented, clicking `Retry checkpoint` in the UI is effectively a no-op.
Suggested fix: make this endpoint execute the same recovery side effects as the normal retry path, but preserve the explicit Check wording and the exhausted-budget guard. Reuse the existing retry logic or extract a shared helper so the endpoint both reports intent and actually retries.

2. Error: `StageStateService` hardcodes the Check repair budget instead of reading the authoritative workflow policy.
File: `packages/cli/src/services/stage-state-service.ts:471-538`
Evidence: `computeCheckRepairState()` sets `const maxAttempts = 1` at line 485. The spec and design require WorkflowRun policy to be the single authoritative source for `review-passed` repair limits. This duplicates policy outside `packages/cli/src/workflow/domain/index.ts:453-455`, so stage-state can drift from runtime behavior if policy changes or historical definitions differ.
Suggested fix: read the Check `review-passed` policy from the workflow definition/projection layer instead of embedding `1` in the read model. If direct access is awkward, introduce a shared exported helper/constant consumed by both WorkflowRun and stage-state projection.

## Spec Compliance

### http-api/spec.md

- PASS: `check-review-repair-state` failed review exposes repair state.
Evidence: `packages/cli/src/services/stage-state-service.ts:525-537`, `packages/cli/tests/stage-state-service.test.ts:506-527`.

- PASS: `check-review-repair-state` repair completion remains separate from review verdict.
Evidence: `packages/cli/src/services/stage-state-service.ts:505-536`, `packages/cli/tests/stage-state-service.test.ts:529-560`.

- PASS: `check-review-repair-state` exhaustion is explicit.
Evidence: `packages/cli/src/services/stage-state-service.ts:489-519`, `packages/cli/tests/stage-state-service.test.ts:548-559`.

- FAIL: `check-review-recovery-actions` retry checkpoint does not schedule exhausted repair.
Evidence: response wording is explicit in `packages/cli/src/api/issues.ts:3385-3392`, but the endpoint does not execute checkpoint retry at all in `packages/cli/src/api/issues.ts:3362-3400`, so the recovery action itself is missing.

- PASS: `check-review-recovery-actions` rerun review only is distinct from repair.
Evidence: `packages/cli/src/api/issues.ts:3406-3492` reruns review and states no repair task will be added.

- PASS: `check-review-recovery-actions` fix review findings is explicit and bounded.
Evidence: `packages/cli/src/api/issues.ts:3498-3558`, `packages/cli/src/services/workflow-application-service.ts:114-171`.

### web-ui/spec.md

- PASS: `check-review-repair-surface` check repair state is visible.
Evidence: `packages/cli/web/src/components/PipelineView.tsx:1117-1197`, rendered from `IssueDetailPage` via `packages/cli/web/src/components/PipelineView.tsx:1434-1435`.

- PASS: `check-review-repair-surface` completed repair followed by failed review is not contradictory.
Evidence: `packages/cli/web/src/components/PipelineView.tsx:1157-1173`, `packages/cli/web/src/components/check-repair-display.test.tsx:183-252`.

- PASS: `check-review-repair-surface` repair exhaustion explains next action.
Evidence: `packages/cli/web/src/components/PipelineView.tsx:1191-1196`, `packages/cli/web/src/components/check-repair-display.test.tsx:255-308`.

- PASS: `check-review-repair-surface` recovery actions use explicit intent labels.
Evidence: `packages/cli/web/src/components/IssueDetailPage.tsx:683-707`, `packages/cli/web/src/components/check-repair-display.test.tsx:312-343`.

- PASS: `check-review-repair-regressions` backend repair projection is covered.
Evidence: `packages/cli/tests/stage-state-service.test.ts:506-629`.

- PASS: `check-review-repair-regressions` exhausted retry does not look like repair.
Evidence: `packages/cli/tests/workflow-run-domain.test.ts:443-486`.
Note: coverage exists at the domain level, but there is no API-level test for the new retry endpoint behavior.

- PASS: `check-review-repair-regressions` frontend display semantics are covered.
Evidence: `packages/cli/web/src/components/check-repair-display.test.tsx:183-343`.

### workflow-run/spec.md

- PASS: `check-review-repair-policy` failed review schedules repair within budget.
Evidence: `packages/cli/src/workflow/domain/index.ts:672-683`, `packages/cli/src/services/workflow-application-service.ts:139-171`, `packages/cli/tests/workflow-run-domain.test.ts:444-463`.

- PASS: `check-review-repair-policy` failed review stops when budget is exhausted.
Evidence: `packages/cli/src/workflow/domain/index.ts:672-692`, `packages/cli/src/services/workflow-application-service.ts:146-149`, `packages/cli/tests/stage-state-service.test.ts:548-559`.

- FAIL: `check-review-repair-policy` WorkflowRun is the authoritative source for repair policy.
Evidence: runtime policy is defined in `packages/cli/src/workflow/domain/index.ts:453-455`, but stage-state projection duplicates `maxAttempts` as a literal in `packages/cli/src/services/stage-state-service.ts:485`.

## Quality Notes

- Complexity: no major complexity violation found in the reviewed paths, but the retry logic is now split across multiple endpoints and should be consolidated to avoid further semantic drift.
- Test coverage: focused tests pass with supported commands:
  - `npx vitest run tests/stage-state-service.test.ts tests/workflow-run-domain.test.ts`
  - `npx vitest run web/src/components/check-repair-display.test.tsx`
- Test command note: `npm test -- --runInBand ...` fails in this repo because the bundled Vitest version does not support `--runInBand`.

## Overall

Overall result: FAIL

<promise>FAIL</promise>
