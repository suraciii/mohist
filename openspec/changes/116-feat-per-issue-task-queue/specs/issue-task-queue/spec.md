## ADDED Requirements

### Requirement: Per-issue task queue with priority insertion

AgentRunnerService SHALL maintain a per-issue FIFO task queue. Each issue SHALL have at most one task executing at a time (running). Additional tasks for the same issue SHALL be queued as pending, ordered by priority (higher priority first), then by enqueue time (FIFO within same priority).

#### Scenario: Enqueue first task for an issue

- **WHEN** `enqueue(issueId, 'start-pipeline', payload)` is called and the issue has no running or pending tasks
- **THEN** the task SHALL be created with status `pending`
- **AND** the scheduler SHALL attempt to start it immediately if a global slot is available

#### Scenario: Enqueue task while issue has a running task

- **WHEN** `enqueue(issueId, 'rebase', payload)` is called and the issue already has a running task
- **THEN** the new task SHALL be created with status `pending` and added to the issue's pending queue
- **AND** the method SHALL return `{ taskId, status: 'pending', queuePosition }`

#### Scenario: Priority insertion into pending queue

- **WHEN** `enqueue(issueId, 'resume-pipeline', payload, { priority: 10 })` is called and the issue has an existing pending task at priority 0
- **THEN** the new task SHALL be inserted before the lower-priority task in the pending queue
- **AND** `queuePosition` for the new task SHALL be 0 (ahead of the existing task)

#### Scenario: FIFO within same priority

- **WHEN** two tasks are enqueued for the same issue with the same priority (default 0) in order A, B
- **THEN** task A SHALL be ahead of task B in the pending queue

### Requirement: Task types and execution semantics

The queue SHALL support three task types: `start-pipeline`, `resume-pipeline`, and `rebase`. Each type maps to a specific execution path within AgentRunnerService. Conflict resolution SHALL NOT be a separate task type — it SHALL be an internal sub-step of the `rebase` task.

#### Scenario: start-pipeline task execution

- **WHEN** a `start-pipeline` task is dequeued and executed
- **THEN** AgentRunnerService SHALL create a worktree (if needed) and start the pipeline from the first stage

#### Scenario: resume-pipeline task execution

- **WHEN** a `resume-pipeline` task is dequeued and executed
- **THEN** AgentRunnerService SHALL resume the pipeline from the issue's current stage

#### Scenario: rebase task execution includes conflict resolution

- **WHEN** a `rebase` task is dequeued and executed
- **THEN** AgentRunnerService SHALL perform rebase on the issue's worktree
- **AND** if conflicts occur, conflict resolution SHALL run as an internal sub-step of the same task
- **AND** the task SHALL NOT complete until rebase (including conflict resolution) finishes

### Requirement: Global concurrency slot limit

The queue SHALL enforce a global concurrency limit (default 8, configurable via `maxConcurrentAgents`). Each running task occupies one slot. When all slots are occupied, pending tasks SHALL wait until a slot is freed.

#### Scenario: Task starts when slot available

- **WHEN** a task is enqueued and `runningSlots.size < maxConcurrentAgents`
- **THEN** the task SHALL transition to `running` and occupy a global slot

#### Scenario: Task waits when all slots occupied

- **WHEN** a task is enqueued and `runningSlots.size >= maxConcurrentAgents`
- **THEN** the task SHALL remain `pending`
- **AND** SHALL be started automatically when a slot is freed

#### Scenario: Slot freed on task completion

- **WHEN** a running task completes (success or failure)
- **THEN** the global slot SHALL be released
- **AND** the scheduler SHALL attempt to start the highest-priority pending task across all issues

#### Scenario: Slot freed on approval gate

- **WHEN** a running pipeline task reaches an approval gate (waiting-design-review or waiting-review)
- **THEN** the task SHALL be marked as completed
- **AND** the global slot SHALL be released
- **AND** the issue SHALL be in a state where a new `resume-pipeline` task can be enqueued via `/approve`

### Requirement: Task cancellation

The queue SHALL support cancelling pending tasks and force-stopping all tasks for an issue.

#### Scenario: Cancel a pending task

- **WHEN** `cancel(taskId)` is called and the task status is `pending`
- **THEN** the task SHALL be removed from the queue
- **AND** the method SHALL return `true`

#### Scenario: Cancel a running task fails

- **WHEN** `cancel(taskId)` is called and the task status is `running`
- **THEN** the task SHALL NOT be cancelled (no preemption)
- **AND** the method SHALL return `false`

#### Scenario: Cancel all tasks for an issue

- **WHEN** `cancelAll(issueId)` is called
- **THEN** all pending tasks for the issue SHALL be cancelled
- **AND** the running task (if any) SHALL be force-stopped (process killed)
- **AND** the global slot SHALL be released

### Requirement: Queue status query

The queue SHALL provide methods to query the current state of tasks.

#### Scenario: Get queue status for a specific issue

- **WHEN** `getQueueStatus(issueId)` is called
- **THEN** the method SHALL return `{ running: Task | null, pending: Task[], queueLength: number }`

#### Scenario: Get queue status for all issues

- **WHEN** `getQueueStatus()` is called without arguments
- **THEN** the method SHALL return `{ totalRunning: number, totalPending: number, maxSlots: number, issues: Map<issueId, IssueQueueStatus> }`

### Requirement: Task queue DB persistence

Task queue state SHALL be persisted to the `issue_task_queue` SQLite table so that it survives server restarts.

#### Scenario: Task persisted on enqueue

- **WHEN** `enqueue()` is called
- **THEN** a row SHALL be inserted into `issue_task_queue` with status `pending`

#### Scenario: Task status updated on state transitions

- **WHEN** a task transitions from `pending` to `running`
- **THEN** the row's `status` SHALL be updated to `running` and `started_at` SHALL be set

#### Scenario: Task status updated on completion

- **WHEN** a task completes (success or failure)
- **THEN** the row's `status` SHALL be updated to `completed` or `failed`, `result` SHALL be set, and `completed_at` SHALL be set

### Requirement: Recovery on server restart

The queue SHALL recover its state from the database when the server restarts.

#### Scenario: Running tasks on restart — awaiting approval

- **WHEN** the server restarts and the DB has tasks with status `running` whose issue is at an approval gate stage (waiting-design-review, waiting-review)
- **THEN** those tasks SHALL be marked as `completed` in the DB
- **AND** the issues SHALL remain at their approval gate stage (no state change)

#### Scenario: Running tasks on restart — mid-execution

- **WHEN** the server restarts and the DB has tasks with status `running` whose issue is NOT at an approval gate stage
- **THEN** those tasks SHALL be marked as `failed` in the DB with result `"Server restarted"`
- **AND** the corresponding issues SHALL be set to status `interrupted`

#### Scenario: Pending tasks on restart

- **WHEN** the server restarts and the DB has tasks with status `pending`
- **THEN** those tasks SHALL be loaded into the in-memory pending queue
- **AND** the scheduler SHALL process them normally (subject to global slot availability)

#### Scenario: Scheduler starts after recovery

- **WHEN** server startup completes and all tasks have been recovered
- **THEN** `schedule()` SHALL be called to start pending tasks that fit within the global slot limit

### Requirement: Enqueue validation

`enqueue()` SHALL perform lightweight validation at enqueue time. Deep validation (e.g., worktree state, branch state) SHALL be deferred to execution time. Duplicate enqueues SHALL be harmless — if the task is no longer relevant at execution time, it SHALL be skipped quickly.

#### Scenario: Enqueue with invalid issue

- **WHEN** `enqueue()` is called with an issueId that does not exist
- **THEN** the method SHALL throw an error (enqueue rejected)

#### Scenario: Enqueue for issue with pending task of same type

- **WHEN** `enqueue(issueId, 'rebase', payload)` is called and there is already a pending `rebase` task for the same issue
- **THEN** the duplicate task SHALL still be enqueued (no deduplication at enqueue time)
- **AND** at execution time, if the rebase is no longer needed, the task SHALL be skipped with a no-op result

#### Scenario: Task skipped at execution time

- **WHEN** a task is dequeued for execution and the preconditions are no longer met (e.g., issue already in `done` stage for a `start-pipeline` task)
- **THEN** the task SHALL be marked as `completed` with result `"skipped"`
- **AND** the global slot SHALL be released immediately
