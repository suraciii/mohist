## MODIFIED Requirements

### Requirement: In-memory session management

The system SHALL support creating agent sessions in memory with: a unique ID, an associated issue ID, a message history (AI SDK CoreMessage[]), a creation timestamp, and a status (active/paused/closed). Sessions SHALL support adding messages. Sessions SHALL support pause/resume lifecycle transitions. Sessions are NOT persisted to SQLite in M1/M2 — server restart loses all session data. The `activeAgents` Map, `pendingGates` Map, and `conflictResolutionInProgress` Set SHALL be removed — all concurrency tracking SHALL go through the per-issue task queue.

#### Scenario: Create session

- **WHEN** a new session is created with an issue ID
- **THEN** the system SHALL generate a unique session ID and store it in memory
- **AND** the session SHALL start with status `active` and an empty message history

#### Scenario: Append message to session

- **WHEN** a message is appended to a session
- **THEN** the message SHALL be added to the session's in-memory history
- **AND** the message SHALL be available for subsequent LLM calls
- **AND** the session MUST NOT be closed (both active and paused sessions accept messages)

#### Scenario: Pause session

- **WHEN** a session is paused
- **THEN** the session status SHALL become `paused`
- **AND** the session messages SHALL be preserved
- **AND** the session SHALL be findable via `findByIssueId()`

#### Scenario: Resume session

- **WHEN** a paused session is resumed
- **THEN** the session status SHALL become `active`
- **AND** the session messages SHALL be preserved

#### Scenario: Close session

- **WHEN** a session is closed
- **THEN** the session status SHALL become `closed`
- **AND** the session SHALL NOT accept new messages (appendMessage throws)
- **AND** the session SHALL NOT be findable via `findByIssueId()`

#### Scenario: Find session by issueId

- **WHEN** `findByIssueId(issueId)` is called
- **THEN** the system SHALL return the session with matching issueId that is active or paused
- **AND** closed sessions SHALL NOT be returned
- **AND** if no matching session exists, return undefined

## ADDED Requirements

### Requirement: AgentRunnerService task queue API

AgentRunnerService SHALL expose enqueue, cancel, cancelAll, and getQueueStatus methods as the primary interface for all mutation operations. The `start()`, `startPipeline()`, `resumePipeline()`, and `forceStop()` methods SHALL be replaced by the queue API.

#### Scenario: enqueue creates and schedules a task

- **WHEN** `enqueue(issueId, taskType, payload, options?)` is called
- **THEN** a task record SHALL be created in the DB with status `pending`
- **AND** the scheduler SHALL be triggered to start the task if a slot is available
- **AND** the method SHALL return `{ taskId, status, queuePosition? }`

#### Scenario: cancel removes a pending task

- **WHEN** `cancel(taskId)` is called and the task is `pending`
- **THEN** the task SHALL be removed from the queue and marked `cancelled` in DB
- **AND** the method SHALL return `true`

#### Scenario: cancelAll stops all tasks for an issue

- **WHEN** `cancelAll(issueId)` is called
- **THEN** all pending tasks SHALL be cancelled
- **AND** the running task SHALL be force-stopped (process killed, slot released)

#### Scenario: getQueueStatus returns queue state

- **WHEN** `getQueueStatus(issueId?)` is called
- **THEN** the method SHALL return the running task and pending queue for the issue (or all issues if no issueId)

### Requirement: AgentRunnerService getStatus returns queue info

`getStatus()` SHALL return task queue information instead of `activeAgents` array.

#### Scenario: getStatus with queue info

- **WHEN** `getStatus()` is called
- **THEN** the return value SHALL include `{ running: number, pending: number, maxSlots: number, tasks: TaskSummary[] }`
- **AND** SHALL NOT include `activeAgents` array or `pendingGates` Map
