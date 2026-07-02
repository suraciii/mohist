### Requirement: Stop clears the current stage's residual approval gate

A stopped workflow run MUST NOT carry a residual awaiting-approval gate on its current stage. When `Stop()` is invoked while the current stage is awaiting approval, the domain SHALL clear that stage's `ApprovalStatus` (set to null) and SHALL transition the stage's `StageRunStatus` away from `AwaitingApproval`. This mirrors the existing approval-invalidation pattern in `AddRuntimeTasks` — stop is the stronger invalidation context (the run terminates outright, so a gate that only exists to gate further progress is meaningless).

#### Scenario: Current stage awaiting approval is cleared on stop

- **WHEN** a workflow run's current stage is awaiting approval (`IsAwaitingApproval` is true) and `Stop()` is called
- **THEN** the current stage's `ApprovalStatus` SHALL be null
- **AND** the current stage's `StageRunStatus` SHALL NOT be `AwaitingApproval`

#### Scenario: Current stage not awaiting approval is unaffected by stop

- **WHEN** a workflow run's current stage is not awaiting approval (e.g. `Running`, `Ready`) and `Stop()` is called
- **THEN** the run SHALL land on `Stopped`
- **AND** the current stage's `ApprovalStatus` SHALL remain unchanged from its pre-stop value

### Requirement: Cleanup is evaluated against runtime state to correct persisted dirty data

The approval-gate cleanup in `Stop()` SHALL be driven by the current stage's runtime state (`IsAwaitingApproval`), not by the arrival of a fresh event. Consequently a run that was persisted with stale awaiting-approval state — such as a run stopped under the pre-fix code that left `ApprovalStatus` dangling — SHALL be corrected the next time its grain executes `Stop()` over that state. This makes the fix self-healing for already-poisoned records without requiring a data migration.

#### Scenario: Persisted dirty run is corrected on next stop

- **WHEN** a run was persisted in a state where `Status` is `Stopped` but the current stage still carries a non-null, unresolved `ApprovalStatus`
- **AND** the run's grain later executes `Stop()` over that persisted state
- **THEN** the current stage's `ApprovalStatus` SHALL be set to null
- **AND** the current stage's `StageRunStatus` SHALL NOT be `AwaitingApproval`

### Requirement: Stop emits no approval-decision events

Stop is termination, not an approval decision. `Stop()` SHALL NOT emit any approval-resolution event such as `StageApprovalResolved`; the only event it emits SHALL remain `WorkflowRunStopped`. Clearing the residual gate removes a stale door state — it does not constitute an approve or reject verdict.

#### Scenario: Stop only produces WorkflowRunStopped

- **WHEN** `Stop()` is called on a run whose current stage is awaiting approval
- **THEN** the returned events SHALL contain `WorkflowRunStopped`
- **AND** SHALL NOT contain any `StageApprovalResolved` event

### Requirement: A stopped run presents no awaiting-approval to downstream consumers

Downstream derived state SHALL NOT report a stopped run as awaiting approval. After `Stop()` clears the current stage's gate, the workflow status view's per-stage `ApprovalStatus` SHALL be null for that stage, and the issue-level derived approval state (DTO `approvalState`, `MohistDefaultWorkflowProjection.StageApprovals`) SHALL NOT present an `awaiting` status for the stopped run. Consumers (status mapper, issue querier, projection, board) need no code change — they read the cleaned domain state.

#### Scenario: Stopped run's stage exposes no approval status

- **WHEN** a workflow run is stopped after its current stage was awaiting approval
- **THEN** the stage status view for that stage SHALL report a null `ApprovalStatus`
- **AND** `MohistDefaultWorkflowProjection.StageApprovals` SHALL yield no approval entry for that stage

#### Scenario: Stopped run's issue DTO reports no awaiting approval

- **WHEN** an issue whose workflow run was stopped mid-approval is queried
- **THEN** the issue DTO's `approvalState` SHALL NOT present an `awaiting` status
