## MODIFIED Requirements

### Requirement: StageDefinition separates static promises from run-owned work sources
StageDefinition SHALL describe static stage tasks/checks, dynamic work sources, and execution or invalidation policies without storing run-specific dynamic task identities.

#### Scenario: Static tasks remain definition promises
- **WHEN** a stage has default required work such as Plan or Integrate tasks
- **THEN** StageDefinition SHALL declare those static tasks and checks as the stage promise
- **AND** WorkflowRun SHALL require matching StageRun evidence before completion

#### Scenario: Build tasks are not copied into static definitions
- **WHEN** Build reads generated tasks from `tasks.json`
- **THEN** StageDefinition MAY describe the dynamic work source and execution policy
- **AND** generated task ids from `tasks.json` SHALL live only as StageRun TaskRun records for that run

#### Scenario: Runtime task kinds are policy not static promises
- **WHEN** runtime work such as `rebase-branch`, repair, retry, or convergence is appended because of this run's facts
- **THEN** StageDefinition MAY define execution or invalidation policy for that kind of work
- **AND** it SHALL NOT list the specific runtime occurrence as a static required task
