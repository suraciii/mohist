# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: warning
  Scope: test-naming
  Evidence: Test `ReconcileStoppedApprovalGate_NoOpOnLiveAwaitingApprovalRun` had "NoOp" in its name, implying the method returns `false` / does nothing on a live run. However, the test body asserts `Assert.True(changed)` — the method **does** clear the gate on a live run (by design, since the guard is `IsAwaitingApproval`, not `run.Status`). The name actively contradicted the method's contract and would confuse future readers. Renamed to `ReconcileStoppedApprovalGate_ClearsGateOnLiveAwaitingApprovalRun`.
  Verification: `dotnet test --filter "FullyQualifiedName~WorkflowRunStatusTransitionSpecs"` — 21 passed, 0 failed.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Workflow/`
  Evidence: Spec requirement 2 scenario 3 asserts "the cleaned state SHALL be written back to the run store" — a grain-level behavior (`OnActivateAsync` → `SaveAsync`). The domain-level test `ReconcileStoppedApprovalGate_CorrectsPersistedDirtyRun` verifies the domain method's state mutation, but there is no grain integration test that verifies `OnActivateAsync` actually calls `SaveAsync` when the reconcile mutates state. The existing reconcile pattern (`ReconcileReadyStatusWithInFlightWork`) also lacks this grain-level test — it is a pre-existing gap in the test suite.
  SuggestedAction: Add a minimal grain-level assertion that a rehydrated `Stopped`+dirty run triggers `SaveAsync` during activation (mirroring whatever test coverage `ReconcileReadyStatusWithInFlightWork` has at the grain level, if any). This was already flagged in the self-review as item-4.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: design / read-path
  Evidence: As noted in `design.md` Open Questions: the issue read path (`IssueQuerier.LoadWorkflowStatesAsync`) reads persisted `State` directly from the DB, bypassing the grain. For a cancelled issue that is never touched again (e.g. #331), the dirty `State` — and thus the stale board "awaiting" — persists until the workflow grain reactivates. The current domain-only fix (D1+D2) addresses all new and in-flight stops, and self-heals on grain activation, but a record that never activates remains dirty indefinitely.
  SuggestedAction: If instant correction for all historical dirty records on the read path is required, add a read-time terminal guard in `MohistDefaultWorkflowProjection.StageApprovals` / `WorkflowStatusMapper.BuildStatusView` to suppress approval presentation when `run.Status` is terminal. Ship domain-only first and observe.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: cleanup
  Scope: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:152-160`
  Evidence: `StopAsync` collects `stopEvents` from `_run.Stop()` (line 152), copies them into a local `events` list (line 155), but never passes `events` to `SaveRunAsync` (line 157 calls the parameterless overload) or `CommitAsync` (line 160 constructs a fresh `[new WorkflowRunStopped()]` instead). The `stopEvents` variable is dead code. Additionally, `WorkflowRunStopped` is constructed twice: once by `_run.Stop()` (line 152) and again by `CommitAsync` (line 160). Only the second is actually published. This is pre-existing and not introduced by this change.
  SuggestedAction: Clean up by removing the dead `stopEvents` / `events` variables and passing the event from `_run.Stop()` directly to `CommitAsync`, or by having `Stop()` return no events (let `CommitAsync` construct the event).
  Status: pre-existing

## Acceptance Criteria Verification

| Criterion | Evidence | Status |
|---|---|---|
| Stage `ApprovalStatus` is null and `StageRunStatus != AwaitingApproval` after stop from approval | `WorkflowRunStatusTransitionSpecs.cs:213-229` `Stop_FromAwaitingApproval_ClearsCurrentStageApprovalGate` — asserts `Null(current.ApprovalStatus)`, `NotEqual(AwaitingApproval, current.Status)`, `Equal(Running, current.Status)` | PASS |
| Stop does not affect non-awaiting stages | `WorkflowRunStatusTransitionSpecs.cs:234-249` `Stop_FromRunningStage_LeavesApprovalStatusUnchanged` — assert `Null(current.ApprovalStatus)` and `NotEqual(AwaitingApproval, current.Status)` after stop from Running | PASS |
| Stop emits only `WorkflowRunStopped`, no approval events | `WorkflowRunStatusTransitionSpecs.cs:254-267` `Stop_EmitsOnlyWorkflowRunStopped_WhenClearingAwaitingApprovalGate` — `Contains(WorkflowRunStopped)`, `DoesNotContain(StageApprovalResolved)`, `DoesNotContain(StageApprovalRequested)` | PASS |
| Persisted dirty run (#331-class) self-heals on grain reactivation | `WorkflowRunStatusTransitionSpecs.cs:272-294` `ReconcileStoppedApprovalGate_CorrectsPersistedDirtyRun` + `WorkflowGrain.cs:83-87` `OnActivateAsync` reconcile + write-back | PASS |
| Idempotent on already-clean runs (no write amplification) | `WorkflowRunStatusTransitionSpecs.cs:299-314` `ReconcileStoppedApprovalGate_IsIdempotentOnAlreadyCleanRun` — first call returns true, second returns false | PASS |
| DTO `approvalState` no longer presents awaiting after stop | Verified via domain state correctness — consumers read cleaned `ApprovalStatus` (null) from domain state, no code change needed in `WorkflowStatusMapper`, `IssueQuerier`, or `MohistDefaultWorkflowProjection` | PASS |

## Spec Compliance

All four requirements and all six scenarios in `openspec/changes/issue-334/specs/workflow-run-stop/spec.md` are covered by the six new test methods in `WorkflowRunStatusTransitionSpecs.cs` (lines 213–339). Each scenario has a dedicated `[Fact]` with explicit Arrange/Act/Assert matching the spec's WHEN/THEN clauses. Edge cases covered include: idempotency across repeated activation, live-run behavior for the `Stop()` call site, and non-awaiting stages being left untouched.

## Cross-cutting Concerns

- **Security:** No injection risk. No new inputs, no new outputs. Only state mutation on existing domain objects.
- **Data safety:** The self-heal writes corrected state back on activation. The reconcile is idempotent — repeated activations are no-ops. Write-back follows the established `ReconcileReadyStatusWithInFlightWork` pattern exactly.
- **Public contracts:** No API / DTO / event contract changes. `Stop()` still returns only `WorkflowRunStopped`. No breaking change.
- **Migration impact:** None. No schema changes, no persistence migration. Existing dirty records self-heal on next grain activation.

<promise>PASS</promise>
