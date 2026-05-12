## MODIFIED Requirements

### Requirement: REQ-WUI-WORKFLOW-RUN-001 Issue Detail renders WorkflowRun-backed progress

Issue Detail SHALL render workflow progress from WorkflowRun-backed data while preserving the existing public model of one task list and one check list per stage. Approval state and diagnostic evidence SHALL remain visually separate from primary task rows.

#### Scenario: Pipeline uses WorkflowRun-backed stage data

- **WHEN** a user opens Issue Detail for a started issue
- **THEN** the pipeline UI SHALL render stages, tasks, checks, and approval from WorkflowRun-backed data
- **AND** it SHALL NOT infer primary progress from session events, logs, or execution history

#### Scenario: Task surfaces agree

- **WHEN** `PipelineView` and `TaskProgressPanel` render the same issue stage
- **THEN** both surfaces SHALL show the same WorkflowRun-backed task list
- **AND** they SHALL NOT disagree because one surface read legacy progress data

#### Scenario: Runtime-added tasks are normal tasks

- **WHEN** a repair, rebase, retry, or conflict-resolution task exists in the WorkflowRun
- **THEN** the UI SHALL render it in the normal stage task list
- **AND** it MAY show available reason or causedBy metadata as explanation
- **AND** it SHALL NOT expose planned, dynamic, or static task categories to the user

#### Scenario: Checks and approval remain separate

- **WHEN** Issue Detail renders stage progress
- **THEN** checks SHALL appear in a check list separate from tasks
- **AND** approval SHALL remain separate decision state rather than a top-level task row
