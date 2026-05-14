## MODIFIED Requirements

### Requirement: Ralph-style task loop execution

The system SHALL preserve Ralph Build execution as a sequential compatibility loop while also exposing the same dynamic Build work as ordered executable tasks that can be executed one task at a time.

#### Scenario: Legacy loop preserves ordered Build execution
- **WHEN** legacy Build callers invoke the Ralph compatibility path
- **THEN** the system reads `tasks.json`
- **AND** validates dependencies before execution starts
- **AND** identifies pending tasks in ascending order
- **AND** executes tasks one at a time until all executable work completes or a failure stops the loop

#### Scenario: Single Build task can execute through shared task runtime
- **WHEN** Build runtime requests one specific pending task
- **THEN** the system loads the ordered executable task list from `tasks.json`
- **AND** selects the requested task without changing task order semantics for other tasks
- **AND** executes only that task through a single-task handler
- **AND** returns a normalized task result that the runner or aggregate can consume

### Requirement: Task failure handling with retry

The system SHALL keep task-owned retry and failure classification behavior when Build task execution is split into loader and handler boundaries.

#### Scenario: Retryable task failure remains handler-owned
- **WHEN** a Build task fails for a retryable reason such as unmet acceptance criteria or environment failure
- **THEN** the task handler classifies the failure using the existing Ralph failure categories
- **AND** stores failure learning for the attempt
- **AND** retries according to the existing category-based retry policy
- **AND** only pauses or fails after the task-owned retry policy is exhausted

#### Scenario: Non-retryable task failure still stops Build work
- **WHEN** a Build task fails for a non-retryable dependency or unrecoverable failure reason
- **THEN** the current task is marked failed
- **AND** later Build tasks do not execute automatically in that loop run
- **AND** the failure remains available for user-action or workflow reporting paths

### Requirement: Task status persistence

The system SHALL persist Build task progress using the same `tasks.json` schema and compatibility exports after the Ralph runtime is split.

#### Scenario: Split runtime preserves tasks.json progress semantics
- **WHEN** a Build task succeeds or fails through the split loader and handler path
- **THEN** the system updates the same `passes`, `attempts`, `error`, and duration progress fields in `tasks.json`
- **AND** compatibility helpers for reading, sorting, and locating pending tasks continue to operate on that file format
- **AND** the split runtime does not require a schema change to `tasks.json`

### Requirement: REQ-RTE-001 Task attempts consume session failure results

Build task execution SHALL treat session liveness failure as a failed task attempt even when task execution is performed through a single-task handler rather than only through the legacy Ralph loop.

#### Scenario: Session failure remains task-owned in split execution
- **WHEN** a single Build task execution receives a session failure result
- **THEN** the current task attempt is recorded as failed
- **AND** the task is not marked passed from partial output alone
- **AND** retry, pause, or failure handling is decided by Ralph task policy rather than by the session runtime itself
