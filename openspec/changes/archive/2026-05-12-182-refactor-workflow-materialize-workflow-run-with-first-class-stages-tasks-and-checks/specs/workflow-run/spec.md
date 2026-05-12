## ADDED Requirements

### Requirement: REQ-WR-001 Starting an issue creates a WorkflowRun

Starting an issue SHALL create one active WorkflowRun runtime record bound to the issue id and issue number. The WorkflowRun SHALL expose stable identity, status, current stage, timestamps, starter metadata, ordered StageRuns for `plan`, `build`, `check`, and `integrate`, and initial Plan task/check instances.

#### Scenario: Start creates seeded run

- **WHEN** an issue is started
- **THEN** the system SHALL create or reuse one active WorkflowRun for that issue
- **AND** the WorkflowRun SHALL have `status = running` and `currentStage = plan`
- **AND** it SHALL contain ordered StageRuns for `plan`, `build`, `check`, and `integrate`
- **AND** the Plan StageRun SHALL contain tasks `proposal`, `specs`, `design`, `tasks`, and `self-review`
- **AND** the Plan StageRun SHALL contain checks `proposal-complete`, `specs-complete`, `design-complete`, `tasks-valid`, `self-review-passed`, and `user-approval`

#### Scenario: Start is idempotent for active run

- **WHEN** start or resume code encounters an issue that already has a non-terminal active WorkflowRun
- **THEN** it SHALL reuse that WorkflowRun
- **AND** it SHALL NOT create a duplicate active run for the same issue

### Requirement: REQ-WR-002 Build tasks materialize into the WorkflowRun

After Plan produces and validates `tasks.json`, Build tasks SHALL be materialized as Task instances under the same WorkflowRun's Build StageRun. Build execution MAY continue using `tasks.json` as executor input, but user-facing Build task state SHALL be stored in WorkflowRun tasks.

#### Scenario: Tasks file becomes Build task instances

- **WHEN** Plan has produced a valid `tasks.json`
- **THEN** the system SHALL create or update Build StageRun task instances in the active WorkflowRun for each task in the file
- **AND** repeated materialization SHALL NOT create duplicate task rows for the same task id

#### Scenario: Build execution updates WorkflowRun tasks

- **WHEN** Ralph executes, skips, completes, or fails a Build task
- **THEN** the corresponding WorkflowRun task SHALL reflect the latest status, attempts, artifacts, and output
- **AND** the primary user-facing Build task list SHALL NOT be reconstructed from logs, checkpoints, or session events

### Requirement: REQ-WR-003 Runtime-added work is represented as normal tasks

Runtime-added repair, rebase, retry, rerun, and conflict-resolution work SHALL be appended to the current StageRun as ordinary WorkflowRun tasks. Such tasks MAY include `reason` and `causedBy` metadata, but SHALL NOT create a user-visible planned/dynamic/static task category.

#### Scenario: Runtime task includes explanation metadata

- **WHEN** a check failure, task failure, branch change, conflict, retry, user action, or system policy creates additional executable work
- **THEN** the work SHALL appear in the same StageRun task list as other tasks
- **AND** it SHOULD include `reason` and `causedBy` metadata identifying why it was added

#### Scenario: Origin metadata is not a user-facing category

- **WHEN** WorkflowRun tasks are returned through API or rendered in UI
- **THEN** users SHALL see one task list for the stage
- **AND** users SHALL NOT need to interpret planned, dynamic, or static task categories

### Requirement: REQ-WR-004 Evidence and checkpoints remain separate from WorkflowRun state

WorkflowRun SHALL be the current runtime state root. `stage_executions`, `workflow_log`, session logs, and checkpoints SHALL retain their existing evidence, audit, or resume-cursor roles and SHALL NOT be used as the primary source for current tasks and checks.

#### Scenario: Logs are evidence only

- **WHEN** the UI or API needs current stage, task, check, or approval state
- **THEN** it SHALL read WorkflowRun state
- **AND** it SHALL NOT reconstruct that current state from `workflow_log`, session logs, or `stage_executions`

#### Scenario: Checkpoint is resume cursor only

- **WHEN** the workflow resumes after interruption
- **THEN** checkpoint data MAY determine the safe resume point
- **AND** checkpoint data SHALL NOT replace WorkflowRun current stage, task, or check state
