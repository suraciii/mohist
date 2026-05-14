## MODIFIED Requirements

### Requirement: REQ-WR-003 Runtime-added work is represented as normal tasks

Runtime-added repair, rebase, retry, rerun, and conflict-resolution work SHALL be appended to the current StageRun as ordinary WorkflowRun tasks. User-triggered rebase SHALL use a visible `rebase-branch` task in the current stage instead of a hidden queue-only execution path.

#### Scenario: User-triggered rebase appears as current stage work

- **WHEN** a user triggers rebase for a non-Done issue with an active WorkflowRun
- **THEN** the system SHALL append `rebase-branch` to the current StageRun task list with title `Rebase branch`
- **AND** the task SHALL carry reason and causedBy metadata explaining why it was added
- **AND** if a `rebase-branch` task in `pending` or `running` state already exists in that StageRun, the system SHALL NOT append a duplicate task

#### Scenario: Approval-paused stage can execute appended rebase task

- **WHEN** the current StageRun is awaiting approval
- **AND** the system appends `rebase-branch` as new executable work
- **THEN** the StageRun SHALL return to `running` so `nextWork()` can schedule the task
- **AND** prior approval state SHALL remain evidence until later invalidation policy decides whether it is still valid
