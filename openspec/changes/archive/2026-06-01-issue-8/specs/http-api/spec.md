## ADDED Requirements

### Requirement: Workflow task APIs expose required files
Issue workflow timeline, stage-state, and WorkflowRun-backed task responses SHALL expose required file metadata for tasks that define `with.expect.files`. The API SHALL expose metadata needed for review while loading file content only on demand through the scoped file-content endpoint.

#### Scenario: Timeline exposes task required files
- **WHEN** a client requests workflow timeline or WorkflowRun-backed progress for an issue whose task defines expected files
- **THEN** each affected task response SHALL include required file entries
- **AND** each required file entry SHALL include at least `path`, `source`, content availability, and marker requirements when declared
- **AND** `source` SHALL be `task-expect` for files projected from task expectations

#### Scenario: Stage-state exposes task required files
- **WHEN** a client requests `GET /api/issues/:number/stage-state`
- **AND** a returned task has expected files
- **THEN** the task SHALL include those required file entries in the canonical task read model
- **AND** the response SHALL NOT embed file content in the task payload

#### Scenario: File content remains scoped and on demand
- **WHEN** a client opens a required file from Issue Detail
- **THEN** the client SHALL fetch current worktree content through an issue-scoped file-content API such as `GET /api/issues/:number/workflow/file-content?path=...`
- **AND** the API SHALL enforce project and issue scope before returning content

### Requirement: Issue list exposes current-stage task progress
`GET /api/issues` SHALL expose a compact workflow stage progress summary for board cards without requiring clients to fetch one workflow timeline per issue. The summary SHALL be derived server-side from the current stage read model and SHALL count user-facing tasks only.

#### Scenario: Active stage progress is returned in issue list
- **WHEN** a client requests `GET /api/issues`
- **AND** an issue has an active workflow stage with user-facing tasks
- **THEN** the issue item SHALL include `workflowStageProgress`
- **AND** the progress SHALL include `stage`, `completed`, and `total`
- **AND** it MAY include `running`, `failed`, and `currentTaskTitle` when available

#### Scenario: Progress excludes internal orchestration tasks
- **WHEN** the current stage contains both user-facing tasks and orchestration/internal tasks
- **THEN** `workflowStageProgress.total` SHALL count only user-facing tasks
- **AND** orchestration/internal tasks SHALL NOT increase completed, running, failed, or total user-task counts

#### Scenario: Hidden progress states are omitted or de-emphasized
- **WHEN** an issue is backlog, done, cancelled, has no user-facing current-stage tasks, or is only waiting for approval/checks
- **THEN** the issue list response MAY omit `workflowStageProgress` or return an empty progress value that clients can de-emphasize

#### Scenario: Failed tasks are not completed progress
- **WHEN** a current-stage user-facing task is failed and has not been superseded by a successful retry
- **THEN** `workflowStageProgress.completed` SHALL NOT include that failed task
- **AND** the failed count SHALL be exposed separately when available
