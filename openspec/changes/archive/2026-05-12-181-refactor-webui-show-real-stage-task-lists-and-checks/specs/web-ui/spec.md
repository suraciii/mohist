## MODIFIED Requirements

### Requirement: REQ-WUI-001 Pipeline UI shows explicit fix tasks

The pipeline UI SHALL render the canonical stage task list returned by the backend stage-state API, including runtime-added repair tasks and excluding obsolete placeholder tasks that never execute.

#### Scenario: Plan shows only real tasks

- **WHEN** the Plan stage has both obsolete placeholder rows and real artifact task data available
- **THEN** the UI SHALL show only the real Plan tasks such as `proposal`, `specs`, `design`, `tasks`, and `self-review`
- **AND** it SHALL NOT show placeholder tasks such as `Read context files` or `Design solution`

#### Scenario: Runtime-added task is explained

- **WHEN** the stage task list includes a runtime-added repair or retry task
- **THEN** the UI SHALL render that task in the same task list as the original stage work
- **AND** it SHALL surface any available explanation metadata such as `Added after Review passed failed`

### Requirement: REQ-WUI-004 Issue Detail uses unified stage state

Issue Detail SHALL use one shared stage-state response as the source of truth for primary task and check progress. `PipelineView` and `TaskProgressPanel` SHALL present the same task list for the same stage, and checks SHALL remain visually separate from tasks.

#### Scenario: Task surfaces stay consistent

- **WHEN** a user views the same issue stage in `PipelineView` and `TaskProgressPanel`
- **THEN** both surfaces SHALL render the same canonical task list from stage-state
- **AND** they SHALL NOT disagree because one surface read placeholder or legacy progress data

#### Scenario: Checks are not promoted to tasks

- **WHEN** Issue Detail renders a stage with task progress, checks, and approval state
- **THEN** checks SHALL appear in a separate checks section
- **AND** approval SHALL remain separate from top-level task entries
- **AND** session activity, logs, and diagnostic evidence SHALL remain supporting detail rather than additional tasks
