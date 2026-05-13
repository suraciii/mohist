# OpenSpec Capability: workflow-run

### Requirement: REQ-WR-001 Starting an issue creates a WorkflowRun

Starting an issue SHALL create or reuse one active WorkflowRun aggregate bound to the issue id and issue number. The aggregate SHALL derive its first stage from its ordered stage definition, create ordered StageRuns for `plan`, `build`, `check`, and `integrate`, seed static Plan and Integrate task/check state, and start the first StageRun without using `Issue.stage` as the state-machine decision source.

#### Scenario: Start creates aggregate-rooted run

- **WHEN** an issue is started
- **THEN** the system SHALL create or reuse one active WorkflowRun for that issue
- **AND** the WorkflowRun SHALL have `status = running` and `currentStage` equal to the first configured runnable stage
- **AND** the first StageRun SHALL be running
- **AND** issue stage/status updates SHALL be projections of the WorkflowRun decision

#### Scenario: Start is idempotent for active run

- **WHEN** start or resume code encounters an issue that already has a non-terminal active WorkflowRun
- **THEN** it SHALL reuse that WorkflowRun
- **AND** it SHALL NOT create a duplicate active run for the same issue

### Requirement: REQ-WR-002 Build tasks materialize into the WorkflowRun

After Plan produces and validates `tasks.json`, Build tasks SHALL be materialized as TaskRun instances under the same WorkflowRun's Build StageRun. `tasks.json` MAY remain the design artifact and Build input, but runtime task progress, skipped/completed/failed state, attempts, artifacts, output, and failure evidence SHALL be stored in WorkflowRun tasks.

#### Scenario: Tasks file becomes Build task instances

- **WHEN** Plan has produced a valid `tasks.json`
- **THEN** the system SHALL create or update Build StageRun task instances in the active WorkflowRun for each task in the file
- **AND** repeated materialization SHALL NOT create duplicate task rows for the same task id

#### Scenario: Build execution updates WorkflowRun tasks

- **WHEN** Ralph executes, skips, completes, or fails a Build task
- **THEN** the corresponding WorkflowRun task SHALL reflect the latest status, attempts, artifacts, and output
- **AND** the primary user-facing Build task list SHALL NOT be reconstructed from `tasks.json`, logs, checkpoints, or session events

### Requirement: REQ-WR-003 Runtime-added work is represented as normal tasks

Runtime-added repair, rebase, retry, rerun, and conflict-resolution work SHALL be appended to the current StageRun as ordinary WorkflowRun tasks. Such tasks SHALL include `reason` and `causedBy` metadata when they are scheduled by a task or check failure policy.

#### Scenario: Fix task records origin

- **WHEN** a failed check schedules a repair task
- **THEN** the repair SHALL appear in the same StageRun task list as other tasks
- **AND** it SHALL record causedBy metadata identifying the originating check or task

#### Scenario: Origin metadata is not a user-facing category

- **WHEN** WorkflowRun tasks are returned through API or rendered in UI
- **THEN** users SHALL see one task list for the stage
- **AND** users SHALL NOT need to interpret planned, dynamic, static, or fix task categories

### Requirement: REQ-WR-004 Evidence and checkpoints remain separate from WorkflowRun state

WorkflowRun SHALL be the current runtime state root and consistency boundary. `stage_executions`, `workflow_log`, session logs, check suites, `stage_states`, and checkpoints SHALL retain evidence, audit, compatibility projection, or resume-cursor roles and SHALL NOT be used as the primary source for current stage, task, check, approval, or failure decisions.

#### Scenario: Logs and projections are evidence only

- **WHEN** the UI, API, or recovery logic needs current stage, task, check, approval, or failure state
- **THEN** it SHALL read WorkflowRun state when a WorkflowRun exists
- **AND** it SHALL NOT reconstruct that current state from logs, `stage_executions`, check suites, `stage_states`, or checkpoints

#### Scenario: Checkpoint is resume cursor only

- **WHEN** the workflow resumes after interruption
- **THEN** checkpoint data MAY determine the safe external resume point
- **AND** checkpoint data SHALL NOT replace WorkflowRun current stage, task, or check state

### Requirement: REQ-WR-005 Integrate runtime work is first-class WorkflowRun state

Integrate stage progress SHALL be represented in WorkflowRun using standard task and check entities. The Integrate StageRun SHALL expose ordered tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge`, plus check `health:integrate`; merge delivery metadata and post-merge freeze state SHALL be persisted as WorkflowRun facts.

#### Scenario: Integrate stage is seeded with visible work

- **WHEN** an issue starts or resumes with an active WorkflowRun
- **THEN** the Integrate StageRun SHALL contain pending tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` in execution order
- **AND** it SHALL contain a pending check `health:integrate`

#### Scenario: Integrate merge records delivery facts

- **WHEN** `integrate:merge` completes successfully
- **THEN** the task result SHALL record `targetBranch`, `baseSha`, `candidateHeadSha`, `landedSha`, and `rebased` when available
- **AND** the Integrate StageRun SHALL record a freeze point that prevents later automatic code-modifying tasks

#### Scenario: Post-merge health failure is non-repairable

- **WHEN** `health:integrate` fails after `integrate:merge` has completed
- **THEN** WorkflowRun SHALL fail with reason `post-merge-health-failed`
- **AND** it SHALL NOT schedule `fix-integrate-health` regardless of check failure policy configuration

