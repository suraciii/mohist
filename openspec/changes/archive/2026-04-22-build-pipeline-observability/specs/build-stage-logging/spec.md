## ADDED Requirements

### Requirement: Build stage change detection logging
`runPipelineBuildStage` SHALL log the result of `detectOpenSpecChange` with the worktree path, issue number, and whether a change was found.

#### Scenario: Change detected
- **WHEN** `detectOpenSpecChange` returns a valid `OpenSpecChange`
- **THEN** the system logs at INFO level: change path, tasks path, issue number
- **AND** emits `build_stage_started` SSE event

#### Scenario: No change found
- **WHEN** `detectOpenSpecChange` returns null
- **THEN** the system logs at WARN level: worktree path and issue number
- **AND** returns immediately with `success: false`

### Requirement: Build stage tasks snapshot logging
`runPipelineBuildStage` SHALL log the state of tasks.json before and after ralph loop execution.

#### Scenario: Log tasks snapshot before build
- **WHEN** ralph loop is about to start
- **THEN** the system logs at INFO level: total task count, pending count (`passes: false`), passed count (`passes: true`)
- **AND** emits `build_tasks_snapshot` SSE event

#### Scenario: Log ralph loop result
- **WHEN** ralph loop completes
- **THEN** the system logs at INFO level: completed, failed, total, success, duration
- **AND** emits `build_stage_completed` or `build_stage_failed` SSE event

### Requirement: Build stage zero-work detection
`runPipelineBuildStage` SHALL detect and log when no tasks were actually executed.

#### Scenario: All tasks already passed before build
- **WHEN** ralph loop returns `completed: 0` and `total > 0` and `success: true`
- **THEN** the system logs at WARN level: "Build completed with 0 tasks executed out of N total — tasks may have been pre-marked as passes"
- **AND** emits `build_stage_failed` SSE event with reason "zero_work"
- **AND** returns `success: false` with an explanatory message

#### Scenario: Normal build execution
- **WHEN** ralph loop returns `completed > 0`
- **THEN** no warning is emitted

### Requirement: Build status API
The system SHALL provide an API endpoint for querying current build status.

#### Scenario: Query build status
- **WHEN** user calls `GET /api/issues/:number/build-status`
- **THEN** the system returns the current build stage, progress, and task list
- **AND** the response includes: stage, status, progress (completed/failed/total/currentTask), tasks array

#### Scenario: Query tasks
- **WHEN** user calls `GET /api/issues/:number/tasks`
- **THEN** the system returns the current tasks.json state
- **AND** the response includes task id, title, status, passes, attempts, error

### Requirement: Event persistence
Critical events SHALL be persisted to workflow_log for audit and recovery.

#### Scenario: Build events persisted
- **WHEN** build stage starts, completes, or fails
- **THEN** the system writes a workflow_log entry with event_type "build_started", "build_completed", or "build_failed"
- **AND** the data field contains the full event payload

#### Scenario: Task events persisted
- **WHEN** a task starts, completes, fails, or retries
- **THEN** the system writes a workflow_log entry with event_type "task_started", "task_completed", "task_failed", or "task_retrying"
