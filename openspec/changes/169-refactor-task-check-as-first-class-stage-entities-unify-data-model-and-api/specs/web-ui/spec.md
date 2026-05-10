## MODIFIED Requirements

### Requirement: REQ-WUI-001 Pipeline UI shows explicit fix tasks

The pipeline UI SHALL render explicit fix tasks from persisted current stage task state. Dynamic fix tasks SHALL be visible even when they are not part of static stage task definitions.

#### Scenario: Health fix task is visible

- **WHEN** current stage state contains `fix-build-health`, `fix-check-health`, or `fix-plan-health`
- **THEN** the task SHALL be displayed in the task list
- **AND** an empty artifact list SHALL NOT hide or invalidate the task

#### Scenario: Review fix task is visible

- **WHEN** current stage state contains `fix-review-findings`
- **THEN** the task SHALL be displayed in the task list
- **AND** its transient output MAY be used for diagnostic display

### Requirement: REQ-WUI-004 Issue Detail uses unified stage state

Issue Detail task/check progress UI SHALL use the unified stage-state API as its primary data source. `PipelineView` and `TaskProgressPanel` SHALL render consistent task state for the same issue and SHALL NOT independently derive primary current progress from `/tasks`, `/build-status`, or `/executions`.

#### Scenario: Pipeline and task panel agree

- **WHEN** a user opens Issue Detail for an issue with task progress
- **THEN** `PipelineView` and `TaskProgressPanel` SHALL render task state from the same stage-state response
- **AND** they SHALL NOT show contradictory completion for the same stage task data

#### Scenario: Retried stage shows current state

- **WHEN** a stage has multiple execution attempts
- **THEN** Issue Detail SHALL show the current latest task/check state
- **AND** it SHALL NOT show the first execution attempt as active progress

#### Scenario: Frontend does not own stage task definitions

- **WHEN** Issue Detail renders plan, check, or integrate tasks
- **THEN** the task definitions SHALL come from backend stage state
- **AND** frontend hardcoded stage task definition arrays SHALL NOT be the source of truth for current progress

#### Scenario: Execution history is audit-only

- **WHEN** Issue Detail displays execution history
- **THEN** it SHALL be clearly separate from primary task/check progress
- **AND** hiding or omitting execution history SHALL NOT affect primary current-state rendering
