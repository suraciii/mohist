## ADDED Requirements

### Requirement: Build dynamic tasks execute through config-driven work source

The Build stage SHALL consume Ralph dynamic tasks through the configured Build work source and Ralph task handler when running under the config-driven stage runner. The system SHALL NOT introduce a separate temporary Build-only execution loop that bypasses WorkflowRun task selection or the shared task runtime.

#### Scenario: Build materializes Ralph tasks before selection

- **WHEN** Build starts or resumes under the config-driven runner and `tasks.json` contains executable Ralph tasks
- **THEN** the Build work source SHALL materialize those tasks into the Build StageRun
- **AND** WorkflowRun SHALL select the next Build task from the materialized StageRun task list

#### Scenario: Build task executes through Ralph handler

- **WHEN** WorkflowRun selects a Build task materialized from Ralph task data
- **THEN** the config-driven runner SHALL execute that task through the Ralph task handler
- **AND** the handler result SHALL update the corresponding WorkflowRun task state
- **AND** later Build work SHALL be selected by WorkflowRun rather than by a runner-local loop

### Requirement: Build migration preserves Ralph resume and checkpoint behavior

The config-driven Build path SHALL preserve existing Ralph task execution, retry, checkpoint, and materialization semantics while moving orchestration under the generic runner.

#### Scenario: Build resumes from materialized task state

- **WHEN** Build resumes after interruption or failed task recovery
- **THEN** the config-driven path SHALL use WorkflowRun task state and compatible checkpoint data to continue from the correct pending or failed Build task
- **AND** it SHALL NOT duplicate tasks that were already materialized from `tasks.json`

#### Scenario: Aggregate single Build task execution remains supported

- **WHEN** aggregate workflow execution requests one specific Build task
- **THEN** the config-driven runner SHALL execute only that requested Build task through the Ralph handler
- **AND** it SHALL report that task result before WorkflowRun selects any subsequent Build task or health check

### Requirement: Build health repair remains ordinary task work

The config-driven Build path SHALL run the Build health gate as a configured check after required Build tasks complete, and any allowed health fix SHALL be scheduled and executed as ordinary task work.

#### Scenario: Build health failure schedules configured fix task

- **WHEN** all required Build tasks have completed and `health:build` fails with an applicable repair policy
- **THEN** WorkflowRun SHALL append the configured Build health repair task to the Build StageRun
- **AND** the config-driven runner SHALL execute the repair task through the shared task runtime before `health:build` is evaluated again

#### Scenario: Build health remains blocked by failed tasks

- **WHEN** any Build task has failed or is not terminal
- **THEN** WorkflowRun SHALL NOT select `health:build`
- **AND** the Build stage SHALL remain blocked by the task state rather than by runner-local health gate control flow
