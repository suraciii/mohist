## Findings

1. Error: `approveStage()` bypasses the shared stage completion guard and can mark a stage passed without re-checking required evidence.
File: `packages/cli/src/workflow/domain/index.ts:1023-1038, 1390-1405`
Why it matters: the spec requires explicit completion paths to enforce the same guard as `nextWork()`. `approveStage()` updates approval state and then calls `completeStage()` directly, while `completeStage()` does not run `evaluateStageCompletionGuard()`. If a stage reaches `awaiting-approval` and its evidence later becomes incomplete or stale before approval is submitted, approval can still advance the workflow. That violates:
- `workflow-engine/spec.md` Scenario: Explicit completion uses the domain guard
- `workflow-run/spec.md` Acceptance criterion: `WorkflowRun.completeStage()` or equivalent final completion path enforces the same completion guard as `nextWork()`.
Suggested fix:
- In `packages/cli/src/workflow/domain/index.ts`, change `approveStage()` to route through `maybeCompleteStage()` or re-run `evaluateStageCompletionGuard()` before calling `completeStage()`.
- Keep `completeStage()` private and evidence-blind only if every caller proves the guard first; otherwise fold the guard into `completeStage()` itself.

## Correctness

- FAIL: approval completion path can bypass the domain evidence guard.

## Complexity

- PASS: the new guard logic is centralized in `evaluateStageCompletionGuard()` and related helpers rather than scattered across multiple paths.

## Test Coverage

- WARN: coverage is strong for guard behavior, Build materialization, runtime rebase work, Integrate evidence, persistence, and projection (`packages/cli/tests/workflow-run-domain.test.ts`, `packages/cli/tests/build-workflowrun-tasks.test.ts`, `packages/cli/tests/integrate-workflowrun.test.ts`, `packages/cli/tests/workflowrun-e2e.test.ts`).
- WARN: I did not run the test suite in this review, so pass/fail of the full suite is unverified.
- FAIL: there is no regression test proving approval cannot advance when evidence becomes incomplete after entering `awaiting-approval`; current tests only cover the happy path for `approveStage()`.

## Security

- PASS: no obvious injection or secret-handling regressions found in the reviewed workflow changes.

## Spec Compliance

- PASS: `nextWork()` / stage completion logic does not treat an empty required task/check set as successful completion.
Evidence: `packages/cli/src/workflow/domain/index.ts:1248-1249, 1295-1305`; tests in `packages/cli/tests/workflow-run-domain.test.ts:156-186`.

- PASS: Static stage tasks/checks declared by `StageDefinition` must exist in `StageRun` before the stage can pass.
Evidence: `packages/cli/src/workflow/domain/index.ts:1295-1305`; tests in `packages/cli/tests/workflow-run-domain.test.ts:156-186`.

- PASS: Build with missing, invalid, or zero-task `tasks.json` does not advance as completed without a clear workflow reason.
Evidence: `packages/cli/src/workflow/domain/index.ts:1307-1313`; materialization state recording in `packages/cli/src/workflow/config-driven-stage-runner.ts:471-504`; tests in `packages/cli/tests/workflow-run-domain.test.ts:188-204`.

- PASS: Dynamic Build tasks generated from `tasks.json` are materialized into the Build `StageRun` and become required for that run.
Evidence: `packages/cli/src/workflow/domain/index.ts:853-871`; `packages/cli/src/workflow/domain/index.ts:387-402`; persistence in `packages/cli/src/workflow/domain/persistence.ts:167-199`; tests in `packages/cli/tests/build-workflowrun-tasks.test.ts:90-191` and `packages/cli/tests/workflow-run-repo.test.ts:168-192`.

- PASS: Runtime-added tasks such as `rebase-branch` are not static `StageDefinition.tasks`, but once appended to a `StageRun` they must complete successfully before the stage can pass.
Evidence: runtime task resolution in `packages/cli/src/workflow/config-driven-stage-runner.ts:590-616`; append and pending guard in `packages/cli/src/workflow/domain/index.ts:874-895, 1325-1327`; tests in `packages/cli/tests/workflow-run-domain.test.ts:1300-1328`.

- PASS: Check cannot pass without a current authoritative review task/result and required review/merge checks.
Evidence: `packages/cli/src/workflow/domain/index.ts:1315-1318, 1360-1387`; stale-snapshot test in `packages/cli/tests/workflow-run-domain.test.ts:784-813`.

- PASS: Integrate cannot complete the workflow without required Integrate delivery evidence.
Evidence: `packages/cli/src/workflow/domain/index.ts:1320-1357`; tests in `packages/cli/tests/workflow-run-domain.test.ts:893-923` and `packages/cli/tests/integrate-workflowrun.test.ts:184-287`.

- FAIL: `WorkflowRun.completeStage()` or equivalent final completion path enforces the same completion guard as `nextWork()`.
Evidence: `nextWork()` checks `evaluateStageCompletionGuard()` at `packages/cli/src/workflow/domain/index.ts:1248-1249`, but `approveStage()` calls `completeStage()` directly at `packages/cli/src/workflow/domain/index.ts:1032-1038`, and `completeStage()` itself has no guard at `packages/cli/src/workflow/domain/index.ts:1390-1405`.

- PASS: `WorkflowRunProjection` defensively refuses impossible passed snapshots, including passed workflows that did not reach the final stage.
Evidence: `packages/cli/src/services/workflow-run-projection.ts:97-137`; tests in `packages/cli/tests/workflowrun-e2e.test.ts:150-186`.

- PASS: A stale failed session on an otherwise successful later workflow run does not prevent `Done`.
Evidence: projection logic does not consult session status in `packages/cli/src/services/workflow-run-projection.ts:53-137`; session handling tests in `packages/cli/tests/workflow/workflow-session-handling.test.ts:135-422`.

- PASS: Regression tests cover empty static stage work, missing dynamic Build work, runtime-added pending work, stale session plus successful task evidence, and impossible passed projection snapshots.
Evidence: `packages/cli/tests/workflow-run-domain.test.ts:156-204, 1300-1328`; `packages/cli/tests/workflowrun-e2e.test.ts:150-186`; `packages/cli/tests/workflow/workflow-session-handling.test.ts:135-422`.

## Overall

- FAIL: one error-level invariant bypass remains in the explicit approval completion path.

<promise>FAIL</promise>
