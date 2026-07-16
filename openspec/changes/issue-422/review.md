# Review Report

## Result: FAIL

The normal recovery flow satisfies the issue's three acceptance criteria: fresh tasks dispatch `recoveryRemaining: null`, runner self-retries keep `recovery.budget` unchanged while decrementing the separate state, and manual retry creates a fresh task definition projection. However, malformed runner input can partially mutate an active workflow before rejection, and required persistence and boundary coverage is missing.

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowWorkLifecycle.cs:30`, `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowWorkLifecycle.cs:73`, `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Work.cs:179`
  Evidence: The report path verifies only that recovery follow-ups have a non-null `recoveryRemaining`, then completes the active task before `MakeContinuationTask` checks whether the value is in range. A report with budget 2 and remaining 3 therefore completes the current task, then throws during follow-up insertion. `ReceiveTaskReportAsync` exits before commit (`WorkflowGrain.Reports.cs:46`), leaving the grain's in-memory run mutated but the database unchanged. The runner's retry then receives `Stale`, while any later save can persist the partial completion without its intended follow-ups. This violates the fail-closed allowance invariant and risks losing the recovery round. [disallowed: data safety and product behavior]
  SuggestedAction: Pre-validate every follow-up, including `0 <= recoveryRemaining <= recovery.budget`, before calling `CompleteTask`, or construct all continuation `TaskRun` values before mutating the run. Add grain specs for negative, above-budget, and mixed valid/invalid batches that assert the active task remains running and persisted state is unchanged after rejection.
  Verification: `npm test` currently passes but does not cover an out-of-range report through this lifecycle.
  Status: unresolved

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.UnitTests/Workflow/Domain/WorkflowRecoveryRoundTests.cs:59`, `packages/server/tests/Mohist.Server.UnitTests/Workflow/Domain/WorkflowRecoveryRoundTests.cs:105`
  Evidence: Manual retry is tested with a hand-built new-format `TaskRun`, and legacy normalization is tested only as raw JSON transformation. No test loads a persisted pre-change 2 -> 1 -> 0 chain through `WorkflowRunStore`, retries the exhausted attempt, and verifies the next saved task has canonical budget 2 with an explicit null fresh marker. This is the reported production failure and is explicitly required by the approved spec.
  SuggestedAction: Add a store/grain spec seeded with a valid legacy `WorkflowRuns.State` row. Load it through the production store path, fail/retry the exhausted attempt, save/reload it, and assert canonical declarations and `recoveryRemaining` values across every historical and new attempt.
  Verification: `npm test` passes, but the required persisted legacy-retry scenario is absent.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:118`, `packages/server/tests/Mohist.Server.UnitTests/Workflow/Domain/WorkflowRecoveryRoundTests.cs:208`
  Evidence: The only server serialization assertion constructs `WorkDispatchResponse` directly. It does not exercise `/api/runner/{runnerId}/poll`, where the route maps `WorkDispatch` to that DTO and must preserve explicit null and numeric continuation state. The design specifically requires poll DTO coverage for both values.
  SuggestedAction: Add a runner poll API spec that dispatches a fresh recovery task and a continuation task, then asserts `recoveryRemaining` is present as `null` and `1` respectively in the HTTP JSON response.
  Verification: `npm test` passes, but no changed server test covers the actual poll route's recovery-state mapping.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/runner/src/server/connection.ts:357`, `packages/runner/tests/server-connection-report.spec.ts:76`
  Evidence: The runner poll mapper correctly retains own-property presence, but its regression test covers only explicit null and absence. It never proves that a numeric continuation value survives the server-to-runner mapping, which is the value that prevents an automatic retry from restarting a recovery round.
  SuggestedAction: Extend the poll fixture with `recoveryRemaining: 1` and assert both its value and own-property presence on the rendered work item.
  Verification: `npm test -w packages/runner` passes, but this continuation mapping is not asserted.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- None.

<promise>FAIL</promise>
