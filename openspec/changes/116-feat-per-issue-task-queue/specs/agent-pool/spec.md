## MODIFIED Requirements

### Requirement: Concurrent agent execution

The AgentRunnerService SHALL support running multiple tasks concurrently up to the configured `maxConcurrentAgents` limit via global concurrency slots managed by the per-issue task queue. Each running task occupies one global slot. When all slots are occupied, new tasks SHALL remain `pending` until a slot is freed.

#### Scenario: Start task under capacity

- **WHEN** `enqueue()` is called and `runningSlots.size < maxConcurrentAgents`
- **THEN** the task SHALL start immediately and occupy a global slot

#### Scenario: Start task at capacity

- **WHEN** `enqueue()` is called and `runningSlots.size >= maxConcurrentAgents`
- **THEN** the task SHALL be added to the pending queue
- **AND** `enqueue()` SHALL return `{ taskId, status: 'pending', queuePosition }` without blocking

#### Scenario: Task completes with pending tasks waiting

- **WHEN** a running task completes and there are pending tasks across all issues
- **THEN** the highest-priority pending task SHALL be started automatically
- **AND** within the same priority level, tasks SHALL be processed in FIFO order by enqueue time

### Requirement: Per-issue agent tracking

The AgentRunnerService SHALL track task state by issueId via the per-issue task queue. The `activeAgents` Map, `pendingGates` Map, and `conflictResolutionInProgress` Set SHALL be removed. All tracking SHALL go through the queue's `getQueueStatus()`.

#### Scenario: Check if specific issue has a running task

- **WHEN** `getQueueStatus(issueId)` is called
- **THEN** the system SHALL return the running task for that issue (or null) and the pending queue

#### Scenario: Check if any task is running

- **WHEN** `getQueueStatus()` is called without arguments
- **THEN** the system SHALL return `totalRunning > 0` if any task is active

#### Scenario: Get status of all active tasks

- **WHEN** `getQueueStatus()` is called
- **THEN** the system SHALL return all running and pending tasks across all issues
- **AND** include total running, total pending, and max slots

### Requirement: Queue processing

Pending tasks SHALL be processed in priority order (higher first), with FIFO as tiebreaker, when a global slot becomes available. Each issue SHALL have at most one running task at a time.

#### Scenario: Priority ordering across issues

- **WHEN** global slot is freed and pending tasks exist for multiple issues with different priorities
- **THEN** the task with the highest priority SHALL be started first

#### Scenario: FIFO within same priority

- **WHEN** two tasks have the same priority, enqueued in order A then B
- **AND** a slot becomes available
- **THEN** task A SHALL be started first

#### Scenario: Per-issue serialization

- **WHEN** a pending task is the highest priority globally but its issue already has a running task
- **THEN** the scheduler SHALL skip that task and check the next highest-priority pending task

#### Scenario: Queue position in response

- **WHEN** a task is enqueued and remains pending
- **THEN** the response SHALL indicate the task's position in the per-issue pending queue
