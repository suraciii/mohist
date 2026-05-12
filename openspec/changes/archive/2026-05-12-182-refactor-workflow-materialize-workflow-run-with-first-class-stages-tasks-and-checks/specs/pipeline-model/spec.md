## MODIFIED Requirements

### Requirement: REQ-PM-WORKFLOW-RUN-001 Pipeline current state is rooted in WorkflowRun

Pipeline current state SHALL be represented by a WorkflowRun containing StageRuns, Tasks, Checks, and approval snapshots. Issue stage/status remain coarse issue fields, `stage_executions` and logs remain evidence, and checkpoints remain resume cursors.

#### Scenario: Current state has one runtime root

- **WHEN** a user or API consumer asks where an issue run currently is
- **THEN** the system SHALL answer from WorkflowRun status, currentStage, StageRuns, tasks, checks, and approval snapshots
- **AND** it SHALL NOT require consumers to combine issue stage, `tasks.json`, stage-state rows, execution logs, session logs, and checkpoints to understand current progress

#### Scenario: Stage organizes tasks and checks

- **WHEN** a stage contains task progress, check results, and approval state
- **THEN** tasks SHALL remain tasks
- **AND** checks SHALL remain checks
- **AND** the stage SHALL only organize those runtime records

#### Scenario: Evidence and resume cursor keep narrow roles

- **WHEN** audit, debug, or resume behavior needs supporting data
- **THEN** `stage_executions`, `workflow_log`, session logs, and checkpoints MAY be used for their existing roles
- **AND** they SHALL NOT define the primary current-state model
