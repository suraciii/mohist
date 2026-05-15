## MODIFIED Requirements

### Requirement: REQ-BDA-REBASE-001 Drift-driven rebase uses visible WorkflowRun tasks

WorkflowRun SHALL schedule drift-driven rebase work only as a visible `rebase-branch` task in the current stage and SHALL deduplicate pending or running rebase tasks.

#### Scenario: Safe window enqueues rebase task

- **WHEN** a drifted issue reaches a safe rebase window
- **AND** policy chooses automatic scheduling
- **THEN** WorkflowRun SHALL append `rebase-branch` to the current StageRun
- **AND** the task SHALL include caused-by metadata explaining base drift

#### Scenario: Pending rebase is not duplicated

- **WHEN** a drifted issue already has a pending or running `rebase-branch` task
- **THEN** WorkflowRun SHALL NOT append another `rebase-branch` task

#### Scenario: Approval-paused stage reopens for rebase work

- **WHEN** a drifted issue is awaiting approval
- **AND** policy schedules `rebase-branch`
- **THEN** the StageRun SHALL return to executable work state
- **AND** the rebase SHALL appear in the normal task list before later checks or approvals continue
