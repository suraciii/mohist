## ADDED Requirements

### Requirement: Build stage emits stage_task_update per task

RalphExecutor SHALL emit a `stage_task_update` SSE event for each task state transition (started, completed, failed, retrying) in addition to the existing `ralph_task_update` event. The `stage_task_update` payload SHALL include `{ issueId, projectId, stage: 'build', taskId, taskTitle, status, attempt, artifacts }`.

#### Scenario: Build task starts and emits stage_task_update

- **WHEN** RalphExecutor begins executing task T-001
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'build', taskId: 'T-001', taskTitle: '<task title>', status: 'started', attempt: 1, artifacts: [] }`

#### Scenario: Build task completes and emits stage_task_update

- **WHEN** RalphExecutor completes task T-001 successfully
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'build', taskId: 'T-001', status: 'completed', attempt: 1 }`

#### Scenario: Build task retries and emits stage_task_update

- **WHEN** task T-002 fails and a retry is initiated
- **THEN** a `stage_task_update` event with `{ status: 'failed', attempt: 1 }` is emitted
- **AND** when the retry begins, a `stage_task_update` event with `{ status: 'retrying', attempt: 2 }` is emitted

### Requirement: Build stage writes StageTaskResult per task

RalphExecutor SHALL write a `StageTaskResult` to `stage_executions.task_results` for each task completion (success or failure). The `task_results` column SHALL accumulate entries as tasks complete, storing `StageTaskResult[]`.

#### Scenario: Build task T-001 completes and result is persisted

- **WHEN** task T-001 completes successfully after 2 attempts and 120 seconds
- **THEN** a `StageTaskResult { taskId: 'T-001', title: '...', status: 'completed', artifacts: [...], attempts: 2, duration: 120000 }` is appended to `stage_executions.task_results`

#### Scenario: Build task T-003 fails and result is persisted

- **WHEN** task T-003 fails after 3 attempts
- **THEN** a `StageTaskResult { taskId: 'T-003', title: '...', status: 'failed', artifacts: [], attempts: 3, duration: <elapsed> }` is appended to `stage_executions.task_results`
