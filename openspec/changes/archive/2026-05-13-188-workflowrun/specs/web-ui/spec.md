## MODIFIED Requirements

### Requirement: REQ-WUI-WORKFLOW-RUN-001 Issue Detail renders WorkflowRun-backed progress

Issue Detail SHALL render workflow progress from WorkflowRun-backed data while preserving the public model of one task list and one check list per stage. Approval state, failure reason, delivery metadata, and diagnostic evidence SHALL remain visually separate from primary task rows.

#### Scenario: Pipeline uses WorkflowRun-backed stage data

- **WHEN** a user opens Issue Detail for a started issue
- **THEN** the pipeline UI SHALL render stages, tasks, checks, approval, and failure state from WorkflowRun-backed data
- **AND** it SHALL NOT infer primary progress from session events, logs, execution history, or `tasks.json`

#### Scenario: Task surfaces agree

- **WHEN** `PipelineView` and `TaskProgressPanel` render the same issue stage
- **THEN** both surfaces SHALL show the same WorkflowRun-backed task list
- **AND** they SHALL NOT disagree because one surface read legacy progress data

#### Scenario: Runtime-added tasks are normal tasks

- **WHEN** a repair, rebase, retry, or conflict-resolution task exists in the WorkflowRun
- **THEN** the UI SHALL render it in the normal stage task list
- **AND** it MAY show available reason or causedBy metadata as explanation
- **AND** it SHALL NOT expose planned, dynamic, static, or fix categories as separate task lists

#### Scenario: Checks and approval remain separate

- **WHEN** Issue Detail renders stage progress
- **THEN** checks SHALL appear in a check list separate from tasks
- **AND** approval SHALL remain separate decision state rather than a top-level task row

### Requirement: REQ-WUI-005 Integrate progress is visible in Issue Detail

Issue Detail progress surfaces SHALL render Integrate from persisted WorkflowRun task and check state so users can see which integration step is running, which steps completed, whether final verification passed or failed, and whether merge delivery has already happened.

#### Scenario: Integrate tasks are visible while running

- **WHEN** the active stage is Integrate and task state is available
- **THEN** Issue Detail SHALL display `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` as separate tasks in order
- **AND** it SHALL show current running, completed, or failed status for each task

#### Scenario: Delivery metadata is visible after merge

- **WHEN** `integrate:merge` has completed
- **THEN** Issue Detail SHALL show delivery metadata including landed sha when available
- **AND** it SHALL not require users to inspect logs to know that merge occurred

#### Scenario: Final health is shown as a check, not a task

- **WHEN** Integrate check state includes `health:integrate`
- **THEN** Issue Detail SHALL render that item in the checks section rather than the task list
- **AND** it SHALL show pass/fail state and diagnostic evidence separately from Integrate task progress
