# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: warning
  Scope: frontend npm audit during targeted test build
  Evidence: The targeted `dotnet test` command invokes the frontend build and reported 8 npm audit findings: 3 moderate, 2 high, and 3 critical. The build and all targeted tests completed successfully, and this dependency audit output is unrelated to the task lifecycle/recovery change.
  SuggestedAction: Triage frontend dependency vulnerabilities separately.
  Status: out-of-scope

## Review Evidence

- Acceptance criteria: `TaskRun` now carries `StartedAt`, `FinishedAt`, `RunnerId`, and `WorkId` at `packages/server/src/Mohist.Server/Workflow/Domain/Run/TaskRun.cs:18`; dispatch enters `Running` and emits `TaskStarted` at `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Task.cs:9`; terminal transitions require `Running` at `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Task.cs:26` and `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Task.cs:42`.
- Dispatch/recovery: `WorkflowGrain` no longer has lease state; it recovers active tasks/checks from `TaskRun`/`StageCheck` at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1114`, matches results by running task work id at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1213`, and exposes active work from dispatch metadata at `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:452`.
- Runner loss: timeout and unregister notify affected workflow grains before clearing local work at `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:83` and `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:339`; best-effort continuation is covered by `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain/RunnerFailureSpecs.cs:135`.
- Stage checks: dispatch metadata is stored at `packages/server/src/Mohist.Server/Workflow/Domain/Run/StageCheck.cs:32`, cleared on result/repair paths at `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Check.cs:111` and `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Stage.cs:185`, and reactivation recovery is covered by `packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/CheckRecoverySpecs.cs:15` and `packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/CheckRecoverySpecs.cs:43`.
- Stop/retry behavior: stopped running tasks record failure details without changing workflow stop semantics at `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Task.cs:60` and `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1252`; regression coverage exists at `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain/RunnerFailureSpecs.cs:165`.

## Verification

- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~TaskLifecycleSpecs|FullyQualifiedName~RunnerFailureSpecs|FullyQualifiedName~CheckRecoverySpecs|FullyQualifiedName~WorkflowLeaseActivationSpecs|FullyQualifiedName~WorkflowStateSpecs"` passed: 29/29.
- `git diff --check master...HEAD` passed.
- Source search for `_leaseState|WorkLease|SaveLeaseAsync|ClearAndDeleteLeaseAsync|ClearChecksLeaseAsync|RestoreDispatch` under `packages/server/src/Mohist.Server` returned no files.

<promise>PASS</promise>
