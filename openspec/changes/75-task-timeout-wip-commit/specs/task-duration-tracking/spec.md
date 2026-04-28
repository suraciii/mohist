## ADDED Requirements

### Requirement: Task attempt duration tracking
The system SHALL record the wall-clock duration of each task attempt in milliseconds and persist it to tasks.json.

#### Scenario: Record duration on successful attempt
- **WHEN** a task attempt completes successfully
- **THEN** the executor records `Date.now() - attemptStartTime` in milliseconds
- **AND** appends the duration to the task's `durations` array
- **AND** writes the updated task (with `durations`) to tasks.json

#### Scenario: Record duration on failed attempt
- **WHEN** a task attempt fails (any category including timeout)
- **THEN** the executor records `Date.now() - attemptStartTime` in milliseconds
- **AND** appends the duration to the task's `durations` array
- **AND** writes the updated task (with `durations`) to tasks.json

#### Scenario: Duration reflects actual execution time not timeout threshold
- **WHEN** a task times out after 28 minutes of actual execution (with a 30-minute timeout threshold)
- **THEN** the recorded duration is 28 * 60 * 1000 milliseconds
- **AND NOT** 30 * 60 * 1000 milliseconds

#### Scenario: Multiple attempts accumulate durations
- **WHEN** a task undergoes 3 attempts with durations 28m, 15m, and 12m
- **THEN** the task's `durations` array is `[1680000, 900000, 720000]`
- **AND** tasks.json reflects the complete array

### Requirement: Duration visibility via API
The system SHALL expose task `durations` through its API endpoints.

#### Scenario: /tasks endpoint returns durations
- **WHEN** a client calls `GET /:number/tasks`
- **THEN** each task object in the response includes its `durations` array if any attempts were made
- **AND** tasks with no attempts have no `durations` field

#### Scenario: /build-status endpoint returns durations
- **WHEN** a client calls `GET /:number/build-status`
- **THEN** each task object in the `tasks` array includes its `durations` array if any attempts were made