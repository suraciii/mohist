## ADDED Requirements

### Requirement: Ralph task execution reports work item attempt outcomes

Ralph Build task execution SHALL report each task dispatch through the WorkflowRun work item attempt model. A Ralph task attempt SHALL start before agent dispatch and SHALL end as completed, failed, or interrupted according to the actual task outcome.

#### Scenario: Ralph starts a running task attempt

- **WHEN** Ralph selects a Build task for execution
- **THEN** the corresponding WorkflowRun task work item SHALL start a `running` latest attempt before the agent session is dispatched
- **AND** the attempt SHALL include runtime evidence identifiers when available

#### Scenario: Ralph failed task remains retryable

- **WHEN** Ralph receives a genuine failed task result
- **THEN** the latest task attempt SHALL become `failed`
- **AND** retry SHALL target that failed work item attempt using existing downstream reset behavior

#### Scenario: Ralph stopped task becomes interrupted

- **WHEN** the Ralph task's agent session is stopped or lost without a genuine failed task result
- **THEN** the latest task attempt SHALL become `interrupted`
- **AND** retry SHALL NOT be exposed solely because the task execution was interrupted
