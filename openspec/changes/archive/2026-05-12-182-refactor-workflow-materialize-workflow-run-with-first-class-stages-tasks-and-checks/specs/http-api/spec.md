## MODIFIED Requirements

### Requirement: REQ-HTTP-WORKFLOW-RUN-001 API exposes active issue WorkflowRun

The HTTP API SHALL expose the active WorkflowRun for an issue, including run status, current stage, ordered StageRuns, tasks, checks, and approval snapshots. The API SHALL treat WorkflowRun as current state and SHALL NOT reconstruct the response from logs or checkpoints.

#### Scenario: Query active WorkflowRun

- **WHEN** a client requests `GET /api/issues/:number/workflow-run`
- **AND** the issue has an active WorkflowRun
- **THEN** the response SHALL include `issueId`, `issueNumber`, WorkflowRun id, status, currentStage, and ordered StageRuns
- **AND** each StageRun SHALL include its tasks, checks, and approval snapshot when present

#### Scenario: No WorkflowRun exists yet

- **WHEN** a client requests `GET /api/issues/:number/workflow-run`
- **AND** the issue has not been started and has no WorkflowRun
- **THEN** the API SHALL return a clear empty-state or not-found response
- **AND** it SHALL NOT fabricate a WorkflowRun from `stage_executions`, logs, or checkpoints

#### Scenario: Start path creates WorkflowRun

- **WHEN** a client starts an issue through the existing start API
- **THEN** the system SHALL create or reuse the active WorkflowRun before workflow execution begins

### Requirement: REQ-HTTP-WORKFLOW-RUN-002 Stage-state compatibility reads WorkflowRun when available

The existing issue stage-state API SHALL project current stage/task/check progress from WorkflowRun when a WorkflowRun exists. Legacy projection MAY remain available only for issues without WorkflowRun data.

#### Scenario: Stage-state response uses WorkflowRun

- **WHEN** a client requests `GET /api/issues/:number/stage-state`
- **AND** the issue has a WorkflowRun
- **THEN** the response SHALL be projected from WorkflowRun StageRuns, tasks, checks, and approval snapshots
- **AND** it SHALL preserve one task list and one check list per stage

#### Scenario: Evidence is not promoted

- **WHEN** the compatibility response is built
- **THEN** `stage_executions`, `workflow_log`, session logs, and checkpoints SHALL NOT become additional user-visible tasks or checks
