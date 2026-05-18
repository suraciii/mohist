## ADDED Requirements

### Requirement: Work item attempts belong to stage work items

WorkflowRun SHALL model execution attempts on stage work items, not on the workflow run itself. A work item MAY be a task or a check within a StageRun. The latest work item attempt SHALL carry state `running`, `completed`, `failed`, or `interrupted`, attempt number, timestamps, diagnostic output or error details, and runtime evidence identifiers when available.

#### Scenario: Task attempt is persisted and reloaded

- **WHEN** a stage task starts execution
- **THEN** WorkflowRun SHALL record a latest task attempt with state `running`
- **AND** repository reload SHALL preserve that latest attempt and any previous attempt history or equivalent snapshot data

#### Scenario: Check attempt is persisted and reloaded

- **WHEN** a stage check starts execution
- **THEN** WorkflowRun SHALL record a latest check attempt with state `running`
- **AND** repository reload SHALL preserve that latest attempt and any previous attempt history or equivalent snapshot data

#### Scenario: Existing work state synthesizes latest attempts

- **WHEN** existing task or check rows are loaded without explicit attempt history
- **THEN** completed or passed work SHALL project to a completed latest attempt
- **AND** failed or error work SHALL project to a failed latest attempt
- **AND** running work SHALL project to a running latest attempt until reconciliation proves interruption
- **AND** pending work SHALL have no latest attempt until execution starts

### Requirement: Attempt transitions keep work progress consistent

WorkflowRun SHALL update work item progress and latest attempt state through one aggregate transition so task or check status cannot drift from the latest attempt within one save operation.

#### Scenario: Completed attempt completes work

- **WHEN** a task or check attempt completes successfully
- **THEN** the latest attempt state SHALL become `completed`
- **AND** the corresponding task or check progress SHALL become completed or passed according to work item type

#### Scenario: Failed attempt fails work

- **WHEN** a task or check handler produces a genuine failed result
- **THEN** the latest attempt state SHALL become `failed`
- **AND** the corresponding task or check progress SHALL become failed or error according to existing stage policy

#### Scenario: Interrupted attempt leaves work incomplete

- **WHEN** a running task or check attempt is interrupted by stopped or lost execution
- **THEN** the latest attempt state SHALL become `interrupted`
- **AND** the work item SHALL remain incomplete
- **AND** the attempt SHALL NOT be treated as a failed result

### Requirement: Workflow recovery summary is derived from work progress

WorkflowRun SHALL expose a workflow recovery summary derived from current stage work progress and latest attempt state. The summary SHALL include at least `running`, `awaiting-approval`, `waiting-for-recovery`, and `completed`.

#### Scenario: Interrupted latest attempt is not active running

- **WHEN** the current work item's latest attempt is `interrupted`
- **THEN** the workflow recovery summary SHALL be `waiting-for-recovery`
- **AND** user-facing workflow state SHALL NOT claim that the current work is actively running

#### Scenario: Non-running latest attempt cannot project active running

- **WHEN** the current work item's latest attempt is `completed`, `failed`, `interrupted`, or absent
- **THEN** the workflow recovery summary SHALL NOT be `running` unless another current work item has a valid live running attempt

#### Scenario: Rerun creates fresh stage attempts

- **WHEN** the current stage is rerun
- **THEN** the stage's work items SHALL receive fresh execution attempts as they execute
- **AND** rerun SHALL NOT reinterpret interrupted attempts as failed retry attempts
