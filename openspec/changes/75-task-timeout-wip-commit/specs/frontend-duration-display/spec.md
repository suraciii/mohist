## ADDED Requirements

### Requirement: Task duration display in frontend
The system SHALL display task attempt durations in the frontend TaskList component.

#### Scenario: Completed task shows duration
- **WHEN** a task has completed successfully
- **THEN** the TaskList displays a checkmark icon followed by the last duration (e.g., "✓ 15m")

#### Scenario: Failed task shows duration with error indicator
- **WHEN** a task has failed
- **THEN** the TaskList displays an error indicator followed by the last duration (e.g., "✗ 28m")
- **AND** the duration reflects the actual execution time of the failed attempt

#### Scenario: Multi-attempt task shows all durations
- **WHEN** a task has multiple attempts with durations
- **THEN** the TaskList displays the attempt count and total time
- **AND** a tooltip or inline format shows each individual attempt duration

#### Scenario: Currently executing task shows live elapsed time
- **WHEN** a task is currently executing (status: started)
- **THEN** the TaskList displays a live elapsed time counter
- **AND** the counter updates every 500ms
- **AND** the elapsed time is calculated as `Date.now() - taskStartTime`
- **AND** when the task completes or fails, the live timer stops and displays the final duration

### Requirement: Live timing via SSE event handling
The system SHALL track and update live elapsed time for the currently executing task.

#### Scenario: Live timer starts on task started event
- **WHEN** a `ralph_task_update` event is received with status 'started'
- **THEN** the SSE handler records the taskId and startTime locally
- **AND** a setInterval (500ms) begins updating the elapsed time display

#### Scenario: Live timer stops on task completion or failure
- **WHEN** a `ralph_task_update` event is received with status 'completed' or 'failed'
- **THEN** the setInterval is cleared
- **AND** the final duration replaces the live elapsed time display
