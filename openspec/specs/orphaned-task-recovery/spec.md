# OpenSpec Capability: orphaned-task-recovery

### Requirement: Runner heartbeat timeout notifies affected workflows

When a `RunnerGrain` detects heartbeat timeout, it SHALL notify each affected `WorkflowGrain` of abandoned work before clearing its local work tracking and marking itself offline. The notification SHALL identify the runner that can no longer execute workflow-owned work; each workflow SHALL use its own Running task dispatch metadata to identify affected work. This notification is the primary orphan-detection signal; it SHALL NOT depend on the workflow polling the runner.

#### Scenario: Timeout with tracked work notifies workflows

- **WHEN** `HandleTimeoutAsync` fires on a runner that has tracked work
- **THEN** the runner SHALL notify each affected `WorkflowGrain` of the abandoned work
- **AND** the notification SHALL identify the lost runner id for each affected workflow
- **AND** only after notification SHALL the runner clear its local work tracking and go offline

#### Scenario: Timeout with no tracked work skips notification

- **WHEN** `HandleTimeoutAsync` fires on a runner that has no tracked work
- **THEN** the runner SHALL clear its local state and go offline
- **AND** no workflow notification SHALL be required

### Requirement: Runner-lost tasks fail with runner-lost reason

When a `WorkflowGrain` learns that a `Running` task's runner is lost - whether through the runner-death notification or through the heartbeat safety net - the workflow grain SHALL transition the affected `Running` task to `Failed` with reason `runner-lost`. The transition SHALL record `FinishedAt`, emit the normal `TaskFailed` and stage/run failure event sequence, and clear any dispatch state associated with the task. The task SHALL NOT remain in `Running` state after the runner is confirmed lost.

#### Scenario: Notification fails the abandoned Running task

- **WHEN** a workflow grain receives a runner-loss notification for a `Running` task
- **THEN** the task SHALL transition to `Failed` with reason `runner-lost`
- **AND** `FinishedAt` SHALL be recorded
- **AND** the `TaskFailed`, `StageFailed`, and `WorkflowRunFailed` events SHALL be emitted

#### Scenario: Safety net fails the orphaned Running task

- **WHEN** the workflow heartbeat detects a `Running` task whose runner is offline
- **THEN** the task SHALL transition to `Failed` with reason `runner-lost`
- **AND** the same failure event sequence SHALL be emitted as for a notified loss

#### Scenario: Non-running tasks are unaffected by runner loss

- **WHEN** a runner-loss notification or heartbeat check evaluates a `Pending` or already-terminal task
- **THEN** that task SHALL NOT be transitioned to `Failed`
- **AND** only `Running` tasks SHALL be subject to runner-lost failure

### Requirement: Workflow heartbeat safety net detects orphaned running tasks

The workflow heartbeat (`EnsureWorkHeartbeatAsync`) SHALL check whether any `Running` task's runner is currently offline. This check SHALL act as a backup for runner-death notifications that were lost or never delivered. A `Running` task whose runner is offline SHALL be failed with reason `runner-lost`. The heartbeat mechanism itself (reminder registration, interval) SHALL remain unchanged; only what the heartbeat inspects SHALL expand to include runner-liveness checks for `Running` tasks.

#### Scenario: Offline runner fails the running task

- **WHEN** the workflow heartbeat fires
- **AND** a `Running` task's runner is offline
- **THEN** the heartbeat SHALL fail that task with reason `runner-lost`

#### Scenario: Online runner preserves the running task

- **WHEN** the workflow heartbeat fires
- **AND** a `Running` task's runner is online
- **THEN** the task SHALL remain `Running`
- **AND** normal dispatch and heartbeat behavior SHALL continue

### Requirement: Orphan detection uses runner liveness, not per-task staleness TTL

Orphaned-task detection SHALL be driven by runner-liveness propagation (heartbeat timeout and runner status), not by a per-task staleness threshold. The system SHALL NOT fail a task solely because it has been `Running` longer than a fixed duration. Task execution durations vary across work types, and a single TTL cannot correctly classify both short health checks and long implementation tasks.

#### Scenario: Long-running task with live runner is not orphaned

- **WHEN** a `Running` task has exceeded any fixed duration threshold
- **AND** its runner is still online and heartbeating
- **THEN** the task SHALL NOT be failed as orphaned
- **AND** the task SHALL remain `Running`

#### Scenario: Short-running task with dead runner is orphaned

- **WHEN** a `Running` task's runner goes offline regardless of elapsed duration
- **THEN** the task SHALL be detected as orphaned through runner-liveness propagation
- **AND** the task SHALL be failed with reason `runner-lost`
